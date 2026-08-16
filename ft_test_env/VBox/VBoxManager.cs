using System.Diagnostics;

namespace ft_test_env.VBox
{
    public record ProcResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string Combined => (StdOut + "\n" + StdErr).Trim();
    }

    /// <summary>Thin wrapper over VBoxManage.exe. Methods shell out and parse text output.</summary>
    public class VBoxManager
    {
        private readonly string vboxManagePath;

        public VBoxManager(string vboxManagePath)
        {
            this.vboxManagePath = vboxManagePath;
        }

        public bool ToolExists() => File.Exists(vboxManagePath);

        /// <summary>Run VBoxManage; throws if it exits non-zero.</summary>
        public ProcResult Run(params string[] args)
        {
            var result = TryRun(args);
            if (!result.Ok)
            {
                throw new Exception(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim());
            }
            return result;
        }

        public ProcResult TryRun(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = vboxManagePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi) ?? throw new Exception($"Could not start {vboxManagePath}");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new ProcResult(process.ExitCode, stdout, stderr);
        }

        // ---- queries ----

        public bool VmExists(string name) =>
            TryRun("list", "vms").StdOut.Contains($"\"{name}\"", StringComparison.Ordinal);

        public bool VmRunning(string name) =>
            TryRun("list", "runningvms").StdOut.Contains($"\"{name}\"", StringComparison.Ordinal);

        /// <summary>The VM's precise state ("running", "stopping", "poweroff", "saved", ...), or "unknown".
        /// Use this rather than VmRunning when you need the VM to be FULLY off (session lock released) —
        /// a VM drops off the running list before its power-off transition completes.</summary>
        public string VmState(string name)
        {
            var info = TryRun("showvminfo", name, "--machinereadable");
            if (!info.Ok) return "unknown";
            foreach (var line in info.StdOut.Split('\n'))
            {
                if (line.StartsWith("VMState=", StringComparison.Ordinal))
                    return line["VMState=".Length..].Trim().Trim('"', '\r', ' ');
            }
            return "unknown";
        }

        public bool BridgeAdapterExists(string adapterName)
        {
            var listing = TryRun("list", "bridgedifs").StdOut;
            return listing
                .Split('\n')
                .Where(l => l.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                .Any(l => l["Name:".Length..].Trim().Equals(adapterName, StringComparison.OrdinalIgnoreCase));
        }

        public bool MediumRegistered(string path) =>
            TryRun("list", "hdds").StdOut.Contains(path, StringComparison.OrdinalIgnoreCase);

        public bool MediumIsImmutable(string path)
        {
            var info = TryRun("showmediuminfo", "disk", path);
            if (!info.Ok) return false;
            return info.StdOut
                .Split('\n')
                .Any(l => l.StartsWith("Type:", StringComparison.OrdinalIgnoreCase)
                          && l.Contains("immutable", StringComparison.OrdinalIgnoreCase));
        }

        public bool SnapshotExists(string vmName, string snapshotName)
        {
            var info = TryRun("snapshot", vmName, "list", "--machinereadable");
            if (!info.Ok) return false;
            return info.StdOut
                .Split('\n')
                .Any(l => l.StartsWith("SnapshotName", StringComparison.OrdinalIgnoreCase)
                          && l.Contains($"=\"{snapshotName}\"", StringComparison.Ordinal));
        }

        // ---- mutations ----

        public void CloneMediumToVdi(string sourceQcow2, string destVdi) =>
            Run("clonemedium", "disk", sourceQcow2, destVdi, "--format", "VDI");

        /// <summary>Best-effort removal of a disk from the media registry (e.g. the source qcow2 after cloning).</summary>
        public void TryCloseDisk(string path) => TryRun("closemedium", "disk", path);

        /// <summary>Register the disk (idempotent) then mark it immutable so each VM gets a resettable diff image.</summary>
        public void MakeImmutable(string vdiPath)
        {
            if (!MediumRegistered(vdiPath))
            {
                // closemedium/openmedium dance not needed: clonemedium already registered it,
                // but if the disk was created out-of-band, register it now.
                Run("openmedium", "disk", vdiPath);
            }
            Run("modifymedium", "disk", vdiPath, "--type", "immutable");
        }

        public void CreateVm(string name, string osType = "Debian_64") =>
            Run("createvm", "--name", name, "--ostype", osType, "--register");

        public void ConfigureVm(string name, int memoryMb, int cpus, string bridgeAdapter) =>
            Run("modifyvm", name,
                "--memory", memoryMb.ToString(),
                "--cpus", cpus.ToString(),
                "--nic1", "bridged",
                "--bridgeadapter1", bridgeAdapter,
                "--boot1", "disk",
                "--boot2", "dvd");

        public void EnsureSataController(string name)
        {
            // Adding a controller that already exists errors; ignore that case.
            // port 0 = immutable root, port 1 = seed ISO, port 2 = optional data disk (QEMU-host node only)
            var result = TryRun("storagectl", name, "--name", "SATA", "--add", "sata",
                                "--controller", "IntelAhci", "--portcount", "3");
            if (!result.Ok && !result.Combined.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(result.Combined);
            }
        }

        public void AttachImmutableDisk(string name, string vdiPath) =>
            Run("storageattach", name, "--storagectl", "SATA", "--port", "0", "--device", "0",
                "--type", "hdd", "--medium", vdiPath);

        public void AttachSeedIso(string name, string isoPath) =>
            Run("storageattach", name, "--storagectl", "SATA", "--port", "1", "--device", "0",
                "--type", "dvddrive", "--medium", isoPath);

        public void AddSharedFolder(string name, string shareName, string hostPath) =>
            Run("sharedfolder", "add", name, "--name", shareName, "--hostpath", hostPath, "--automount");

        /// <summary>Expose the host's VT-x/AMD-V to the guest so it can run nested KVM. Required by the
        /// QEMU-host node, which runs a nested QEMU/KVM guest for the virtio-fs / virtio-9p tests.</summary>
        public void EnableNestedVirt(string name) =>
            Run("modifyvm", name, "--nested-hw-virt", "on");

        /// <summary>Create a fixed-size data disk (if absent) and attach it to SATA port 2. Used by the
        /// QEMU-host node to hold the (large) nested-guest images off the tiny, immutable, reset-on-reboot
        /// root. Idempotent: skips creation if the .vdi exists and ignores an already-attached medium.</summary>
        public void CreateAndAttachDataDisk(string name, string vdiPath, int sizeMb)
        {
            if (!File.Exists(vdiPath) && !MediumRegistered(vdiPath))
            {
                Run("createmedium", "disk", "--filename", vdiPath, "--size", sizeMb.ToString(), "--format", "VDI");
            }

            var result = TryRun("storageattach", name, "--storagectl", "SATA", "--port", "2", "--device", "0",
                                "--type", "hdd", "--medium", vdiPath);
            if (!result.Ok && !result.Combined.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(result.Combined);
            }
        }

        public void StartVmHeadless(string name) =>
            Run("startvm", name, "--type", "headless");

        public void PowerOff(string name) =>
            Run("controlvm", name, "poweroff");

        /// <summary>Poll until the VM is FULLY powered off (state poweroff, lock released), or the timeout
        /// elapses. Returns true if it stopped.</summary>
        public bool WaitUntilOff(string name, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (VmState(name) == "poweroff") return true;
                Thread.Sleep(2000);
            }
            return false;
        }

        // ---- Windows gold-image linked-clone mutations ----

        /// <summary>Linked-clones the Windows gold VM from a named snapshot into a fresh VM and registers it.
        /// A linked clone is fast and space-cheap (a persistent diff disk over the gold's snapshot) — the
        /// Windows analog of the immutable Debian base, except the diff PERSISTS across reboots so a node's
        /// reconfigured IP/hostname and deployed bits survive a reboot. Re-cloned fresh each bring-up.</summary>
        public void CloneLinkedFromGold(string goldVmName, string goldSnapshot, string newName) =>
            Run("clonevm", goldVmName,
                "--snapshot", goldSnapshot,
                "--options", "link",
                "--name", newName,
                "--register");

        /// <summary>Take a snapshot of the VM's current state (the VM must be off for an offline snapshot).
        /// Retries on a transient LockMachine/E_FAIL, which can occur briefly after power-off.</summary>
        public void TakeSnapshot(string vmName, string snapshotName)
        {
            for (var attempt = 1; ; attempt++)
            {
                var result = TryRun("snapshot", vmName, "take", snapshotName);
                if (result.Ok) return;
                if (attempt >= 6 || !result.Combined.Contains("LockMachine", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(result.Combined);
                Thread.Sleep(2000);
            }
        }

        /// <summary>Delete a snapshot (best effort). Fails if a linked clone still depends on it, so delete
        /// the dependent clones first.</summary>
        public void DeleteSnapshot(string vmName, string snapshotName) =>
            TryRun("snapshot", vmName, "delete", snapshotName);

        /// <summary>Best-effort power off then unregister + delete a VM and its files.</summary>
        public void Unregister(string vmName)
        {
            if (VmRunning(vmName)) TryRun("controlvm", vmName, "poweroff");
            TryRun("unregistervm", vmName, "--delete");
        }
    }
}
