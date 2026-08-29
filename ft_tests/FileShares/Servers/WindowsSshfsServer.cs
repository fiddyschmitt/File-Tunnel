using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>The Windows server VM (.84) as an sshfs server (issue #45): Windows OpenSSH Server exports a dir; the
    /// Android clients sshfs-mount &lt;user&gt;@.84:/C:/sshfs_export (Windows OpenSSH presents drives as /C:/...).
    /// Password auth (the baked lab account). Restart ensures OpenSSH Server is installed + running + the firewall +
    /// the export dir, via runremote (session 1). Only runs in the batched full-lab run (needs the Windows node up).</summary>
    public class WindowsSshfsServer : Server
    {
        public const string Host = "192.168.0.84";
        public const string ExportDir = "/C:/sshfs_export";       // sshfs remote path (Windows OpenSSH drive form)
        private const string ExportDirWin = @"C:\sshfs_export";   // same dir, Windows form

        private readonly ProcessRunner runner;   // win10_x64_2 (the .84 server), runremote

        public WindowsSshfsServer(ProcessRunner runner) : base(OS.Windows, FileShareType.Sshfs)
        {
            this.runner = runner;
        }

        public override void Restart()
        {
            // Ensure OpenSSH Server present + running + firewall + export dir (idempotent). runremote drives session 1.
            var ps =
                "$ErrorActionPreference='SilentlyContinue';" +
                "if((Get-WindowsCapability -Online -Name OpenSSH.Server*).State -ne 'Installed'){Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0};" +
                "Set-Service sshd -StartupType Automatic; Start-Service sshd;" +
                "if(-not(Get-NetFirewallRule -Name ft-sshd)){New-NetFirewallRule -Name ft-sshd -DisplayName 'ft sshd' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22};" +
                $"New-Item -ItemType Directory -Force '{ExportDirWin}' | Out-Null;" +
                $"icacls '{ExportDirWin}' /grant Everyone:(OI)(CI)F | Out-Null";
            runner.RunCommand($"powershell -NoProfile -Command \"{ps}\"");
        }
    }
}
