using ft_test_env.Config;
using ft_test_env.Steps;
using Renci.SshNet;

namespace ft_test_env.Ssh
{
    /// <summary>
    /// The ft-specific, per-clone provisioning done over SSH/SCP after a Windows node has been reconfigured
    /// to its target IP. Almost everything a node needs (runremote autostart in the interactive session,
    /// Client-for-NFS, the 'Shared' SMB share, cmdkey for .81, RDP, autologon, the c_drive VBox shared
    /// folder) is BAKED INTO ft-win-gold (see FT_WIN_GOLD_IMAGE.md) and inherited by every linked clone, so
    /// this step is deliberately thin: stage the CURRENT ft.exe (the gold's copy may be stale), and
    /// re-assert the two bits most worth guarding against drift (the .81 cmdkey and the Shared share).
    /// </summary>
    public class WindowsProvisioner
    {
        private readonly EnvConfig config;

        public WindowsProvisioner(EnvConfig config)
        {
            this.config = config;
        }

        public StepOutcome ProvisionNode(WindowsNodeConfig node)
        {
            var gold = config.WindowsGold;
            try
            {
                // 1. Stage the current ft.exe. The test's RemoteWindowsProcessRunner also SCPs it, but
                //    pre-staging keeps a freshly-cloned node ready and lets CheckNode validate C:\Temp\ft.
                if (File.Exists(gold.FtExeSource))
                {
                    using var ssh0 = new SshClient(node.Ip, gold.SshPort, gold.Username, gold.Password);
                    ssh0.Connect();
                    ssh0.CreateCommand("New-Item -ItemType Directory -Force 'C:\\Temp\\ft' | Out-Null").Execute();

                    using var scp = new ScpClient(node.Ip, gold.SshPort, gold.Username, gold.Password);
                    scp.Connect();
                    scp.Upload(new FileInfo(gold.FtExeSource), "/C:/Temp/ft/ft.exe");
                }

                // 2. Idempotent insurance for the baked bits. `net share` / `cmdkey` error harmlessly when
                //    already present (CreateCommand().Execute() does not throw on a non-zero exit).
                using var ssh = new SshClient(node.Ip, gold.SshPort, gold.Username, gold.Password);
                ssh.Connect();

                // Disable the Windows firewall. The e2e tests connect from the dev-box test runner INTO ft's
                // tunnel-forward listeners on the clients, which land on high ports (5001-5999); the gold's
                // baked firewall only opens 22/445/3389/8888, so those connections are refused ("Could not
                // connect"). A scoped 5000-5999 allow rule proved unreliable here, and this is an isolated
                // throwaway lab, so we just turn the firewall off. (The server VM's firewall is off too - see
                // FT_WIN_GOLD_IMAGE.md "Server VM".)
                ssh.CreateCommand("Set-NetFirewallProfile -All -Enabled False").Execute();

                ssh.CreateCommand($"net share Shared=\"{gold.SharedSharePath}\" /grant:{gold.Username},FULL").Execute();

                // Windows->.81\data needs a cached credential (the non-Mac Smb rows never refresh cmdkey at
                // test time). The .81 SMB share account is the same user/live the Linux nodes SSH with.
                var server = config.Nodes.FirstOrDefault(n => n.IsServer)?.Ip ?? "192.168.0.81";
                ssh.CreateCommand($"cmdkey /add:{server} /user:{config.Linux.Username} /pass:{config.Linux.Password}").Execute();

                return StepOutcome.Ok(File.Exists(gold.FtExeSource) ? "ft.exe staged; Shared + .81 cmdkey asserted"
                                                                    : "Shared + .81 cmdkey asserted (ft.exe source missing)");
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }
    }
}
