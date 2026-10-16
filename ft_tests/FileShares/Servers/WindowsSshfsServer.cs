using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>The Windows server VM as an sshfs server (issue #45): Windows OpenSSH Server exports a dir; the
    /// Android clients sshfs-mount &lt;user&gt;@&lt;server&gt;:/C:/sshfs_export (Windows OpenSSH presents drives as
    /// /C:/...). Password auth (the lab's Windows account). Restart ensures OpenSSH Server is installed + running +
    /// the firewall + the export dir. Only runs when the Windows server VM is up.</summary>
    public class WindowsSshfsServer : Server
    {
        public static readonly string Host = Lab.WindowsServerIp;
        public const string ExportDir = "/C:/sshfs_export";        // sshfs remote path (Windows OpenSSH drive form)
        public const string LocalExportDir = @"C:\sshfs_export";   // same dir, Windows form (a local ft reader on .84 uses this)

        private readonly ProcessRunner runner;   // win10_x64_2 (the Windows server VM)

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
                $"New-Item -ItemType Directory -Force '{LocalExportDir}' | Out-Null;" +
                $"icacls '{LocalExportDir}' /grant Everyone:(OI)(CI)F | Out-Null";
            runner.RunCommand($"powershell -NoProfile -Command \"{ps}\"");
        }
    }
}
