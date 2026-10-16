using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>The Mac as an sshfs server (issue #45): its always-on sshd exports a dir; the Android clients
    /// sshfs-mount &lt;user&gt;@&lt;mac&gt;:~/ftsshfs. Key auth - Restart ensures the export dir and appends the
    /// throwaway sshfs keypair's public key to the Mac's ~/.ssh/authorized_keys (idempotent, no sudo; the Mac's own
    /// login key auth is untouched). Staged base64 to keep the key text intact through C#->SSH->zsh.</summary>
    public class MacSshfsServer : Server
    {
        public static readonly string Host = Lab.MacIp;
        public static readonly string ExportDir = $"{Lab.MacHome}/ftsshfs";
        public static readonly string SshUser = Lab.MacUser;

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
