using System.Diagnostics;
using System.Security.Cryptography;
using ft_test_env.Cloud;
using ft_test_env.Config;
using ft_test_env.Ssh;
using ft_test_env.Steps;
using ft_test_env.VBox;

namespace ft_test_env
{
    /// <summary>Wires VBoxManager + cloud-init seeds + health checks into the menu actions.</summary>
    public class Orchestrator
    {
        private readonly EnvConfig config;
        private readonly VBoxManager vbox;
        private readonly CloudInitSeed seed;
        private readonly LinuxHealthChecks linux;
        private readonly WindowsHealthChecks windows;
        private readonly WindowsProvisioner winProvisioner;
        private readonly MacEmulator macEmulator;

        public Orchestrator(EnvConfig config)
        {
            this.config = config;
            vbox = new VBoxManager(config.VBoxManagePath);
            seed = new CloudInitSeed(config);
            linux = new LinuxHealthChecks(config);
            windows = new WindowsHealthChecks(config);
            winProvisioner = new WindowsProvisioner(config);
            macEmulator = new MacEmulator(config.MacEmulator);
        }

        // ---- 1. one-time prep (idempotent) ----

        public bool Prep()
        {
            var step = new StepRunner();
            step.Section("One-time prep");

            step.Run("VBoxManage present", () => vbox.ToolExists()
                ? StepOutcome.Ok(config.VBoxManagePath)
                : StepOutcome.Fail($"not found at {config.VBoxManagePath}"));

            step.Run("Bridge adapter configured", () =>
            {
                if (string.IsNullOrWhiteSpace(config.Network.BridgeAdapter))
                    return StepOutcome.Fail("Network:BridgeAdapter is empty (see 'VBoxManage list bridgedifs')");
                return vbox.BridgeAdapterExists(config.Network.BridgeAdapter)
                    ? StepOutcome.Ok(config.Network.BridgeAdapter)
                    : StepOutcome.Fail($"adapter '{config.Network.BridgeAdapter}' not found");
            });

            step.Run("Working directory", () =>
            {
                Directory.CreateDirectory(config.WorkingDir);
                return StepOutcome.Ok(config.WorkingDir);
            });

            step.Run($"Debian image ({config.Image.FileName})", DownloadImageIfMissing);

            step.Run("base.vdi", () =>
            {
                if (File.Exists(config.BaseVdiPath)) return StepOutcome.Skip("already exists");
                vbox.CloneMediumToVdi(config.ImagePath, config.BaseVdiPath);
                vbox.TryCloseDisk(config.ImagePath);   // drop the source qcow2 from the registry
                return StepOutcome.Ok("converted from qcow2");
            });

            step.Run("base.vdi immutable", () =>
            {
                if (vbox.MediumIsImmutable(config.BaseVdiPath)) return StepOutcome.Skip("already immutable");
                vbox.MakeImmutable(config.BaseVdiPath);
                return StepOutcome.Ok();
            });

            foreach (var node in config.NodesServerFirst)
            {
                step.Run($"{node.Name}: seed ISO", () =>
                {
                    seed.BuildSeedIso(node, config.SeedIsoPath(node));
                    return StepOutcome.Ok($"{node.Hostname} @ {node.Ip}");
                });

                step.Run($"{node.Name}: VM", () =>
                {
                    if (vbox.VmExists(node.Name)) return StepOutcome.Skip("already registered");
                    vbox.CreateVm(node.Name);
                    // The QEMU-host and build-host nodes get more RAM/CPU than a plain node (a nested KVM guest
                    // and the RAM-hungry NativeAOT ILC respectively).
                    var memoryMb = node.QemuHost ? config.Linux.QemuHostMemoryMb
                                 : node.BuildHost ? config.Linux.BuildHostMemoryMb
                                 : config.Linux.MemoryMb;
                    var cpus = node.QemuHost ? config.Linux.QemuHostCpus
                             : node.BuildHost ? config.Linux.BuildHostCpus
                             : config.Linux.Cpus;
                    vbox.ConfigureVm(node.Name, memoryMb, cpus, config.Network.BridgeAdapter);
                    vbox.EnsureSataController(node.Name);
                    vbox.AttachImmutableDisk(node.Name, config.BaseVdiPath);
                    vbox.AttachSeedIso(node.Name, config.SeedIsoPath(node));
                    vbox.AddSharedFolder(node.Name, "C_DRIVE", @"C:\");
                    if (node.QemuHost)
                    {
                        // Nested virt + a persistent data disk (SATA port 2) so setup_debian.sh can build and
                        // run the nested virtio-fs/9p guest off the tiny immutable root.
                        vbox.EnableNestedVirt(node.Name);
                        vbox.CreateAndAttachDataDisk(node.Name, config.DataDiskPath(node), config.Linux.QemuHostDataDiskMb);
                    }
                    else if (node.BuildHost)
                    {
                        // A persistent data disk (SATA port 2) holds the .NET SDK + Android NDK + build caches -
                        // the immutable root is far too small. No nested virt: it only cross-compiles.
                        vbox.CreateAndAttachDataDisk(node.Name, config.DataDiskPath(node), config.Linux.BuildHostDataDiskMb);
                    }
                    var kind = node.QemuHost ? "created (QEMU host: nested virt + data disk)"
                             : node.BuildHost ? "created (build host: data disk)"
                             : "created";
                    return StepOutcome.Ok(kind);
                });
            }

            PrepWindowsNodes(step);
            PrepAndroidEmulators(step);

            return Summary(step);
        }

        private void PrepWindowsNodes(StepRunner step)
        {
            var gold = config.WindowsGold;
            if (!gold.Enabled)
            {
                step.Run("Windows nodes", () => StepOutcome.Skip("disabled (WindowsGold:Enabled=false)"));
                return;
            }

            // The clones are (re)created from the gold's CURRENT state at every bring-up (so they pick up any
            // gold updates), so prep only checks that the gold image is present.
            step.Run($"{gold.GoldVmName}: gold image present", () => vbox.VmExists(gold.GoldVmName)
                ? StepOutcome.Ok("clones are re-created from its current state at bring-up")
                : StepOutcome.Fail($"gold VM '{gold.GoldVmName}' not found — build it once (see FT_WIN_GOLD_IMAGE.md)"));
        }

        private StepOutcome DownloadImageIfMissing(Action<string> report)
        {
            if (File.Exists(config.ImagePath))
            {
                if (!string.IsNullOrWhiteSpace(config.Image.Sha512))
                {
                    report("verifying checksum");
                    return VerifySha512(config.ImagePath, config.Image.Sha512) == StepStatus.Ok
                        ? StepOutcome.Skip("present, checksum OK")
                        : StepOutcome.Fail("present but checksum mismatch (delete to re-download)");
                }
                return StepOutcome.Skip("already downloaded");
            }

            if (string.IsNullOrWhiteSpace(config.Image.Url))
                return StepOutcome.Fail("Image:Url is empty");

            var tempPath = config.ImagePath + ".part";
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            using (var response = http.GetAsync(config.Image.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                using var src = response.Content.ReadAsStream();
                using var dst = File.Create(tempPath);

                var buffer = new byte[1024 * 1024];
                long copied = 0;
                var sw = Stopwatch.StartNew();
                var lastReportMs = -1000L;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    copied += read;

                    if (sw.ElapsedMilliseconds - lastReportMs >= 250)
                    {
                        report(FormatDownloadProgress(copied, total, sw.Elapsed));
                        lastReportMs = sw.ElapsedMilliseconds;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(config.Image.Sha512))
            {
                report("verifying checksum");
                if (VerifySha512(tempPath, config.Image.Sha512) != StepStatus.Ok)
                {
                    File.Delete(tempPath);
                    return StepOutcome.Fail("checksum mismatch after download");
                }
            }

            File.Move(tempPath, config.ImagePath, overwrite: true);
            return StepOutcome.Ok("downloaded");
        }

        private static string FormatDownloadProgress(long copied, long? total, TimeSpan elapsed)
        {
            const double MB = 1024d * 1024d;
            var copiedMb = copied / MB;
            var speed = elapsed.TotalSeconds > 0 ? copiedMb / elapsed.TotalSeconds : 0;

            if (total is > 0)
            {
                var totalMb = total.Value / MB;
                var pct = copied * 100d / total.Value;
                return $"{pct:F0}% ({copiedMb:F1} / {totalMb:F1} MB, {speed:F1} MB/s)";
            }

            return $"{copiedMb:F1} MB, {speed:F1} MB/s";
        }

        private static StepStatus VerifySha512(string path, string expected)
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
            return hash == expected.Trim().ToLowerInvariant() ? StepStatus.Ok : StepStatus.Failed;
        }

        // ---- 2. bring up environment for a test run ----

        public bool BringUpAll()
        {
            var step = new StepRunner();
            step.Section("Bring up environment");

            var server = config.Nodes.FirstOrDefault(n => n.IsServer);
            // The build host is on-demand (menu 3), not part of the e2e matrix — never brought up here.
            var clients = config.Nodes.Where(n => !n.IsServer && !n.BuildHost).ToList();

            // Provision the server fully first — the clients mount its NFS/SMB exports during
            // their own provisioning, so those exports must already exist.
            if (server != null)
            {
                StartVm(step, server);
                step.Run($"{server.Name}: wait for SSH", () => linux.WaitForSsh(server));
                step.Run($"{server.Name}: provisioning complete", () => linux.WaitForProvisioned(server));
            }

            // Start all client VMs first so their cloud-init (apt install) runs concurrently in
            // the background, then wait for them one at a time to keep console output clean.
            foreach (var client in clients)
            {
                StartVm(step, client);
            }
            foreach (var client in clients)
            {
                step.Run($"{client.Name}: wait for SSH", () => linux.WaitForSsh(client));
                step.Run($"{client.Name}: provisioning complete", () => linux.WaitForProvisioned(client));
            }

            // Windows nodes AFTER the Linux track (deviation from rclone, which runs the two in parallel):
            // the ft-provisioning cmdkeys .81 and the Nfs rows mount X: from .81, so the Linux server must
            // already be up.
            BringUpWindowsNodes(step);

            // Start the hand-built server VM (.84) if it's off - it is not cloned/reconfigured, just started -
            // so the Linux //.84/Shared mount below (and the Windows SMB rows) have a server to reach.
            EnsureServerUp(step);

            // Mount cross-host shares AFTER the server VM is up: the Linux SMB client mount //192.168.0.84/Shared
            // targets it. EnsureMounts is idempotent; already-mounted shares are left as-is.
            foreach (var node in config.NodesServerFirst.Where(n => !n.BuildHost))
            {
                linux.EnsureMounts(step, node);
            }

            // Launch the Mac Android emulators (issue #45) as part of the test-run bring-up, like any other node.
            BringUpAndroidEmulators(step);

            step.Section("Health checks");
            foreach (var node in config.NodesServerFirst.Where(n => !n.BuildHost))
            {
                linux.CheckNode(step, node);
            }
            if (config.WindowsGold.Enabled)
            {
                foreach (var node in config.WindowsNodesOrdered)
                {
                    windows.CheckNode(step, node);
                }
            }
            if (config.WindowsServer.Enabled)
            {
                windows.CheckServer(step, config.WindowsServer);
            }
            CheckAndroidEmulators(step);

            return Summary(step);
        }

        /// <summary>
        /// Brings up the Windows clones fresh from the gold's CURRENT state every run: shut the gold down,
        /// delete the previous clones, snapshot the gold's current state and linked-clone off it (so manual
        /// gold changes flow through), then reconfigure each clone (rename + IP + reboot) SEQUENTIALLY (they
        /// all boot at the gold's SourceIp) and run the thin ft-specific provisioning. The linked-clone diff
        /// is persistent, so a later reboot (RebootNode) keeps a node's config.
        /// </summary>
        private void BringUpWindowsNodes(StepRunner step)
        {
            var gold = config.WindowsGold;
            if (!gold.Enabled) return;

            step.Section("Bring up Windows nodes");

            if (!vbox.VmExists(gold.GoldVmName))
            {
                step.Run($"{gold.GoldVmName}: present", () => StepOutcome.Fail("gold VM not found — see FT_WIN_GOLD_IMAGE.md"));
                return;
            }

            EnsureGoldOff(step, gold);

            // Delete the previous clones so the gold snapshot they pin can be refreshed.
            foreach (var node in config.WindowsNodesOrdered)
            {
                step.Run($"{node.CloneName}: delete previous clone", () =>
                {
                    if (!vbox.VmExists(node.CloneName)) return StepOutcome.Skip("none");
                    vbox.Unregister(node.CloneName);
                    return StepOutcome.Ok("deleted");
                });
            }

            step.Run($"{gold.GoldVmName}: snapshot current state as '{gold.GoldSnapshot}'", () =>
            {
                if (vbox.SnapshotExists(gold.GoldVmName, gold.GoldSnapshot))
                    vbox.DeleteSnapshot(gold.GoldVmName, gold.GoldSnapshot);
                vbox.TakeSnapshot(gold.GoldVmName, gold.GoldSnapshot);
                return StepOutcome.Ok("captured");
            });

            foreach (var node in config.WindowsNodesOrdered)
            {
                step.Run($"{node.CloneName}: linked clone off current gold", () =>
                {
                    vbox.CloneLinkedFromGold(gold.GoldVmName, gold.GoldSnapshot, node.CloneName);
                    return StepOutcome.Ok($"linked clone of {gold.GoldVmName}");
                });
            }

            // Reconfigure + provision each clone, one at a time (they all boot at the gold's SourceIp).
            foreach (var node in config.WindowsNodesOrdered)
            {
                StartReconfigureProvision(step, node, gold);
            }
        }

        /// <summary>
        /// Brings up (or re-brings-up) a SINGLE Windows node from the gold, without touching the others.
        /// Used to finish/repair a partial bring-up and to re-provision one node after a manual gold change —
        /// each run is short (one clone + one reconfigure), unlike the full <see cref="BringUpAll"/>. Clones
        /// off the gold's existing 'clean' snapshot (taking one if absent). Boots the fresh clone at the gold's
        /// SourceIp, so never run two of these concurrently.
        /// </summary>
        public bool BringUpWindowsNode(WindowsNodeConfig node)
        {
            var step = new StepRunner();
            step.Section($"Bring up {node.CloneName} ({node.Ip}) [{node.Role}]");

            var gold = config.WindowsGold;
            if (!gold.Enabled)
            {
                step.Run("Windows nodes", () => StepOutcome.Fail("disabled (WindowsGold:Enabled=false)"));
                return Summary(step);
            }
            if (!vbox.VmExists(gold.GoldVmName))
            {
                step.Run($"{gold.GoldVmName}: present", () => StepOutcome.Fail("gold VM not found — see FT_WIN_GOLD_IMAGE.md"));
                return Summary(step);
            }

            EnsureGoldOff(step, gold);

            step.Run($"{node.CloneName}: delete previous clone", () =>
            {
                if (!vbox.VmExists(node.CloneName)) return StepOutcome.Skip("none");
                vbox.Unregister(node.CloneName);
                return StepOutcome.Ok("deleted");
            });

            step.Run($"{gold.GoldVmName}: snapshot '{gold.GoldSnapshot}' present", () =>
            {
                if (vbox.SnapshotExists(gold.GoldVmName, gold.GoldSnapshot)) return StepOutcome.Skip("reusing existing");
                vbox.TakeSnapshot(gold.GoldVmName, gold.GoldSnapshot);
                return StepOutcome.Ok("captured");
            });

            step.Run($"{node.CloneName}: linked clone off gold", () =>
            {
                vbox.CloneLinkedFromGold(gold.GoldVmName, gold.GoldSnapshot, node.CloneName);
                return StepOutcome.Ok($"linked clone of {gold.GoldVmName}");
            });

            StartReconfigureProvision(step, node, gold);

            step.Section("Health check");
            windows.CheckNode(step, node);

            return Summary(step);
        }

        /// <summary>Ensures the gold is powered off (graceful SSH shutdown, else forced) so it can be snapshotted/cloned.</summary>
        private void EnsureGoldOff(StepRunner step, WindowsGoldConfig gold)
        {
            step.Run($"{gold.GoldVmName}: ensure powered off (graceful)", () =>
            {
                if (!vbox.VmRunning(gold.GoldVmName)) return StepOutcome.Skip("already off");
                var requested = windows.RequestShutdown(gold.SourceIp);
                if (vbox.WaitUntilOff(gold.GoldVmName, gold.ShutdownTimeoutSeconds))
                    return StepOutcome.Ok(requested ? "clean shutdown via SSH" : "shut down");
                vbox.PowerOff(gold.GoldVmName);
                return StepOutcome.Ok("forced off (did not shut down in time)");
            });
        }

        /// <summary>Starts a freshly-cloned node at the gold's SourceIp, reconfigures it (rename + IP + reboot),
        /// confirms the new identity, then runs the thin ft provisioning. Shared by the full and single-node paths.</summary>
        private void StartReconfigureProvision(StepRunner step, WindowsNodeConfig node, WindowsGoldConfig gold)
        {
            step.Run($"{node.CloneName}: start (boots at {gold.SourceIp})", () =>
            {
                vbox.StartVmHeadless(node.CloneName);
                return StepOutcome.Ok();
            });
            step.Run($"{node.CloneName}: wait for SSH at {gold.SourceIp}", () => windows.WaitForSsh(gold.SourceIp, gold.SshReadyTimeoutSeconds));
            step.Run($"{node.CloneName}: reconfigure ({node.Hostname} + {node.Ip} + reboot)", () => windows.LaunchReconfigure(node));
            step.Run($"{node.CloneName}: wait until reconfigured ({node.Hostname} @ {node.Ip})", () => windows.ConfirmReconfigured(node));
            step.Run($"{node.CloneName}: ft provisioning", () => winProvisioner.ProvisionNode(node));
        }

        /// <summary>Starts the hand-built server VM (.84) if it's off. Unlike the clones it is not cloned or
        /// reconfigured - it's a fixed, manually-built VM with a distinct SID - so we only ensure it's running
        /// and SSH-reachable.</summary>
        private void EnsureServerUp(StepRunner step)
        {
            var server = config.WindowsServer;
            if (!server.Enabled) return;

            step.Section("Windows server VM");
            step.Run($"{server.VmName}: running", () =>
            {
                if (!vbox.VmExists(server.VmName))
                    return StepOutcome.Fail($"server VM '{server.VmName}' not registered — build it by hand (see FT_WIN_GOLD_IMAGE.md 'Server VM')");
                if (vbox.VmRunning(server.VmName)) return StepOutcome.Skip("already running");
                vbox.StartVmHeadless(server.VmName);
                return StepOutcome.Ok("started");
            });
            step.Run($"{server.VmName}: wait for SSH at {server.Ip}", () =>
                windows.WaitForSsh(server.Ip, config.WindowsGold.SshReadyTimeoutSeconds));
        }

        // ---- 3. bring up a single node ----

        public bool BringUpNode(NodeConfig node)
        {
            var step = new StepRunner();
            step.Section($"Bring up {node.Name}");

            StartVm(step, node);
            step.Run($"{node.Name}: wait for SSH", () => linux.WaitForSsh(node));
            step.Run($"{node.Name}: provisioning complete", () => linux.WaitForProvisioned(node));

            if (node.BuildHost)
            {
                // The build host runs no lab services or cross-host mounts - just report SSH + toolchain state.
                step.Section("Build host readiness");
                linux.CheckBuildHost(step, node);
                return Summary(step);
            }

            // Mount any shares not already present (idempotent) — lets pressing this again pick up
            // a share whose host has since come online, without a reboot.
            linux.EnsureMounts(step, node);

            step.Section("Health checks");
            linux.CheckNode(step, node);

            return Summary(step);
        }

        private void StartVm(StepRunner step, NodeConfig node)
        {
            step.Run($"{node.Name}: start (pristine, immutable disk resets)", () =>
            {
                if (!vbox.VmExists(node.Name)) return StepOutcome.Fail("VM not registered — run prep first");
                if (vbox.VmRunning(node.Name)) return StepOutcome.Skip("already running");
                vbox.StartVmHeadless(node.Name);
                return StepOutcome.Ok();
            });
        }

        // ---- 4. teardown ----

        public bool Teardown()
        {
            var step = new StepRunner();
            step.Section("Teardown");

            foreach (var node in config.Nodes)
            {
                step.Run($"{node.Name}: power off", () =>
                {
                    if (!vbox.VmExists(node.Name)) return StepOutcome.Skip("not registered");
                    if (!vbox.VmRunning(node.Name)) return StepOutcome.Skip("already off");
                    vbox.PowerOff(node.Name);
                    return StepOutcome.Ok();
                });
            }

            if (config.WindowsGold.Enabled)
            {
                foreach (var node in config.WindowsNodesOrdered)
                {
                    step.Run($"{node.CloneName}: power off", () =>
                    {
                        if (!vbox.VmExists(node.CloneName)) return StepOutcome.Skip("not registered");
                        if (!vbox.VmRunning(node.CloneName)) return StepOutcome.Skip("already off");
                        vbox.PowerOff(node.CloneName);
                        return StepOutcome.Ok();
                    });
                }
            }

            if (config.WindowsServer.Enabled)
            {
                step.Run($"{config.WindowsServer.VmName}: power off", () =>
                {
                    if (!vbox.VmExists(config.WindowsServer.VmName)) return StepOutcome.Skip("not registered");
                    if (!vbox.VmRunning(config.WindowsServer.VmName)) return StepOutcome.Skip("already off");
                    vbox.PowerOff(config.WindowsServer.VmName);
                    return StepOutcome.Ok();
                });
            }
            TeardownAndroidEmulators(step);

            return Summary(step);
        }

        // ---- 5. check Linux services ----

        public bool CheckLinux()
        {
            var step = new StepRunner();
            step.Section("Linux service checks");

            foreach (var node in config.NodesServerFirst)
            {
                if (node.BuildHost) linux.CheckBuildHost(step, node);
                else linux.CheckNode(step, node);
            }

            return Summary(step);
        }

        // ---- 6. check Windows services ----

        public bool CheckWindows()
        {
            var step = new StepRunner();
            step.Section("Windows node checks");

            if (config.WindowsGold.Enabled)
            {
                foreach (var node in config.WindowsNodesOrdered)
                {
                    windows.CheckNode(step, node);
                }
            }

            if (config.WindowsServer.Enabled)
            {
                windows.CheckServer(step, config.WindowsServer);
            }

            // The dev box (.31) and any other external hosts still get their local checks.
            foreach (var host in config.WindowsHosts)
            {
                windows.CheckHost(step, host);
            }

            return Summary(step);
        }

        // ---- 7. check gold image readiness ----

        public bool CheckGold()
        {
            var step = new StepRunner();
            step.Section("Gold image readiness");

            if (!config.WindowsGold.Enabled)
            {
                step.Run("gold image", () => StepOutcome.Skip("disabled (WindowsGold:Enabled=false)"));
                return Summary(step);
            }

            windows.CheckGold(step);
            return Summary(step);
        }

        // ---- 8. reboot a Windows node (clear tiring; config survives the persistent linked-clone diff) ----

        public bool RebootNode(WindowsNodeConfig node)
        {
            var step = new StepRunner();
            step.Section($"Reboot {node.CloneName} ({node.Ip})");

            step.Run($"{node.CloneName}: reboot via SSH", () =>
            {
                if (!vbox.VmExists(node.CloneName)) return StepOutcome.Fail("clone not registered — bring up first");
                if (!vbox.VmRunning(node.CloneName)) return StepOutcome.Fail("not running");
                return windows.RequestShutdown(node.Ip, reboot: true)
                    ? StepOutcome.Ok("reboot requested")
                    : StepOutcome.Fail("could not reach the node over SSH to reboot it");
            });

            // shutdown /r keeps sshd up for a few seconds, so wait for the node to actually go down before
            // waiting for it to come back — otherwise we'd reconnect to the still-running pre-reboot node.
            step.Run($"{node.CloneName}: wait until it goes down", () =>
                windows.WaitForSshDown(node.Ip, 90));

            step.Run($"{node.CloneName}: wait for SSH back at {node.Ip}", () =>
                windows.WaitForSsh(node.Ip, config.WindowsGold.SshReadyTimeoutSeconds));

            // Confirm ft-ready — the persistent diff kept the config; autologon + the baked runremote
            // autostart must have re-established the interactive session + UDP 8888.
            windows.CheckNode(step, node);

            return Summary(step);
        }

        /// <summary>Reboots the hand-built server VM (.84) to clear tiring, then confirms it comes back
        /// SMB/RDP-ready (autologon re-establishes the interactive session + runremote UDP 8888).</summary>
        public bool RebootServer()
        {
            var step = new StepRunner();
            var server = config.WindowsServer;
            step.Section($"Reboot {server.VmName} ({server.Ip})");

            step.Run($"{server.VmName}: reboot via SSH", () =>
            {
                if (!vbox.VmExists(server.VmName)) return StepOutcome.Fail("server VM not registered");
                if (!vbox.VmRunning(server.VmName)) return StepOutcome.Fail("not running");
                return windows.RequestShutdown(server.Ip, reboot: true)
                    ? StepOutcome.Ok("reboot requested")
                    : StepOutcome.Fail("could not reach the server over SSH to reboot it");
            });

            step.Run($"{server.VmName}: wait until it goes down", () => windows.WaitForSshDown(server.Ip, 90));
            step.Run($"{server.VmName}: wait for SSH back at {server.Ip}", () =>
                windows.WaitForSsh(server.Ip, config.WindowsGold.SshReadyTimeoutSeconds));

            windows.CheckServer(step, server);
            return Summary(step);
        }

        // ---- Android emulators on the Mac (issue #45) ----
        // Folded into the standard lifecycle: Setup is part of Prep, Launch part of BringUpAll, Check part of
        // BringUpAll's health checks, Teardown part of Teardown - so the two emulators (both bridged, real LAN IPs)
        // come up and go down with the rest of the lab, gated on MacEmulator:Enabled.

        /// <summary>One-time SDK + AVD setup (part of Prep). Idempotent; the ~1.5 GB download happens once.</summary>
        private void PrepAndroidEmulators(StepRunner step)
        {
            if (!config.MacEmulator.Enabled)
            {
                step.Run("Android emulators", () => StepOutcome.Skip("disabled (MacEmulator:Enabled=false)"));
                return;
            }
            step.Run($"{config.MacEmulator.Host}: Android SDK + AVD setup", () => macEmulator.Setup());
        }

        /// <summary>Launch both emulators + wait for boot (part of BringUpAll).</summary>
        private void BringUpAndroidEmulators(StepRunner step)
        {
            if (!config.MacEmulator.Enabled)
            {
                step.Run("Android emulators", () => StepOutcome.Skip("disabled (MacEmulator:Enabled=false)"));
                return;
            }
            step.Run($"{config.MacEmulator.Host}: launch Android emulators ({config.MacEmulator.Serial} + {config.MacEmulator.SecondSerial}, both bridged)", () => macEmulator.Launch());
        }

        /// <summary>Confirm both emulators are up (part of BringUpAll's health checks; silent when disabled -
        /// the launch step already reported the skip).</summary>
        private void CheckAndroidEmulators(StepRunner step)
        {
            if (!config.MacEmulator.Enabled) return;
            step.Run($"{config.MacEmulator.Host}: Android emulators ({config.MacEmulator.Serial} + {config.MacEmulator.SecondSerial})", () => macEmulator.Check());
        }

        /// <summary>Kill both emulators (part of Teardown).</summary>
        private void TeardownAndroidEmulators(StepRunner step)
        {
            if (!config.MacEmulator.Enabled) return;
            step.Run($"{config.MacEmulator.Host}: kill Android emulators ({config.MacEmulator.Serial} + {config.MacEmulator.SecondSerial})", () => macEmulator.Teardown());
        }

        private static bool Summary(StepRunner step)
        {
            Console.WriteLine();
            var original = Console.ForegroundColor;
            if (step.AllSucceeded)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("All steps succeeded.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("One or more steps FAILED (see above).");
            }
            Console.ForegroundColor = original;
            return step.AllSucceeded;
        }
    }
}
