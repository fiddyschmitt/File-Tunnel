using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>The Mac (.33) as an sshfs server (issue #45): its always-on sshd exports a dir; the Android clients
    /// sshfs-mount smith@.33:/Users/smith/ftsshfs. Key auth - Restart ensures the export dir and appends the lab
    /// keypair's public key to the Mac's ~/.ssh/authorized_keys (idempotent, no sudo; the Mac's own login key auth
    /// is untouched). Staged base64 to keep the key text intact through C#->SSH->zsh.</summary>
    public class MacSshfsServer : Server
    {
        public const string Host = "192.168.0.33";
        public const string ExportDir = "/Users/smith/ftsshfs";
        public const string SshUser = "smith";

        private readonly ProcessRunner runner;    // a Mac runner (mac_1)
        private readonly string authorizedKeyText;

        public MacSshfsServer(ProcessRunner runner, string authorizedKeyText) : base(OS.Mac, FileShareType.Sshfs)
        {
            this.runner = runner;
            this.authorizedKeyText = authorizedKeyText;
        }

        public override void Restart()
        {
            var script = string.Join("\n",
                $"mkdir -p {ExportDir}; chmod 777 {ExportDir}",
                "mkdir -p ~/.ssh; chmod 700 ~/.ssh; touch ~/.ssh/authorized_keys; chmod 600 ~/.ssh/authorized_keys",
                $"grep -qF '{authorizedKeyText}' ~/.ssh/authorized_keys || printf '%s\\n' '{authorizedKeyText}' >> ~/.ssh/authorized_keys");
            var b64 = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            runner.RunCommand($"echo {b64} | base64 -d | bash");
        }
    }
}
