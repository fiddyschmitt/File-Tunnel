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

        public Orchestrator(EnvConfig config)
        {
            this.config = config;
            vbox = new VBoxManager(config.VBoxManagePath);
            seed = new CloudInitSeed(config);
            linux = new LinuxHealthChecks(config);
            windows = new WindowsHealthChecks(config);
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
                    vbox.ConfigureVm(node.Name, config.Linux.MemoryMb, config.Linux.Cpus, config.Network.BridgeAdapter);
                    vbox.EnsureSataController(node.Name);
                    vbox.AttachImmutableDisk(node.Name, config.BaseVdiPath);
                    vbox.AttachSeedIso(node.Name, config.SeedIsoPath(node));
                    vbox.AddSharedFolder(node.Name, "C_DRIVE", @"C:\");
                    return StepOutcome.Ok("created");
                });
            }

            return Summary(step);
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
            var clients = config.Nodes.Where(n => !n.IsServer).ToList();

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

            // Mount any shares not already present (e.g. a Windows host that came online after
            // the nodes booted). Idempotent — already-mounted shares are left as-is.
            foreach (var node in config.NodesServerFirst)
            {
                linux.EnsureMounts(step, node);
            }

            step.Section("Health checks");
            foreach (var node in config.NodesServerFirst)
            {
                linux.CheckNode(step, node);
            }

            return Summary(step);
        }

        // ---- 3. bring up a single node ----

        public bool BringUpNode(NodeConfig node)
        {
            var step = new StepRunner();
            step.Section($"Bring up {node.Name}");

            StartVm(step, node);
            step.Run($"{node.Name}: wait for SSH", () => linux.WaitForSsh(node));
            step.Run($"{node.Name}: provisioning complete", () => linux.WaitForProvisioned(node));

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

            return Summary(step);
        }

        // ---- 5. check Linux services ----

        public bool CheckLinux()
        {
            var step = new StepRunner();
            step.Section("Linux service checks");

            foreach (var node in config.NodesServerFirst)
            {
                linux.CheckNode(step, node);
            }

            return Summary(step);
        }

        // ---- 6. check Windows services ----

        public bool CheckWindows()
        {
            var step = new StepRunner();
            step.Section("Windows service checks");

            foreach (var host in config.WindowsHosts)
            {
                windows.CheckHost(step, host);
            }

            return Summary(step);
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
