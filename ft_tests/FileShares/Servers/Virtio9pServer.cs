using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for a virtio-9p share. The QEMU host (the Linux client2 VM) runs a nested QEMU guest - the
    /// systemd unit <c>ftq-guest</c>, provisioned by the lab's playbook (provisioning\ansible) - which exposes the
    /// host directory <see cref="HostExportDir"/> to the guest over the virtio transport (QEMU <c>-virtfs</c>),
    /// mounted in the guest with <c>-t 9p -o trans=virtio</c>, and forwards the guest's SSH to a port on the host.
    ///
    /// QEMU's -virtfs reports the BACKING filesystem's statfs magic (ext4), NOT V9FS, so guest-side ft does
    /// not auto-detect 9p - it runs plain Normal mode, which is correct because QEMU virtio-9p (cache=none)
    /// is coherent (unlike diod's TCP-9p, which reports V9FS and is incoherent). So both sides run Normal;
    /// nothing forces upload-download. Restart ensures the export dir exists and the nested guest is running.
    /// </summary>
    public class Virtio9pServer : Server
    {
        public static readonly string HostIp = Lab.QemuHostIp;
        public const string HostExportDir = "/srv/ft9p";

        private readonly ProcessRunner processRunner;

        public Virtio9pServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.Virtio9p)
        {
            this.processRunner = processRunner;
        }

        public override void Restart()
        {
            processRunner.Run("bash", $"-c 'mkdir -p {HostExportDir} && chmod 777 {HostExportDir} && (systemctl is-active --quiet ftq-guest || systemctl restart ftq-guest)'");
        }
    }
}
