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

        public void CreateVm(string name) =>
            Run("createvm", "--name", name, "--ostype", "Debian_64", "--register");

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
            var result = TryRun("storagectl", name, "--name", "SATA", "--add", "sata",
                                "--controller", "IntelAhci", "--portcount", "2");
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

        public void StartVmHeadless(string name) =>
            Run("startvm", name, "--type", "headless");

        public void PowerOff(string name) =>
            Run("controlvm", name, "poweroff");
    }
}
