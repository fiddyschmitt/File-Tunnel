using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for a 9P (Plan 9 filesystem protocol) share, served by <c>diod</c> - a userspace
    /// 9P2000.L server - over TCP on .81, mirroring the NFS topology (client1 - server - client2).
    /// diod, its config and the export dir are provisioned by setup_debian.sh: DIOD_ENABLE=true in
    /// /etc/default/diod, and /etc/diod.conf exports <see cref="ExportDir"/> with auth_required=0 so
    /// the in-kernel 9p client can mount over plain TCP (no munge). The client needs no package.
    ///
    /// Restart re-(starts) the daemon and ensures the export dir, clearing a hung mount server-side.
    /// </summary>
    public class NinePServer : Server
    {
        public const string ServerIp = "192.168.0.81";
        public const string ExportDir = "/srv/9p";
        public const int Port = 564;

        private readonly ProcessRunner processRunner;

        public NinePServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.NineP)
        {
            this.processRunner = processRunner;
        }

        public override void Restart()
        {
            processRunner.Run("bash", $"-c 'mkdir -p {ExportDir} && chmod 777 {ExportDir} && systemctl restart diod'");
        }
    }
}
