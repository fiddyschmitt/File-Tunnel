using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for a virtio-fs share. The QEMU host (the Linux client2 VM) runs virtiofsd plus a nested QEMU
    /// guest - the systemd unit <c>ftq-guest</c>, provisioned by the lab's playbook (provisioning\ansible) - which
    /// exposes the host directory <see cref="HostExportDir"/> to the guest as a FUSE-based virtio-fs mount and
    /// forwards the guest's SSH to a port on the host (<see cref="Lab.QemuGuestSshPort"/>).
    ///
    /// The tunnel runs host-side ft (on the native /srv/ftvfs ext4) against guest-side ft (on the virtio-fs
    /// mount). ft distinguishes virtio-fs from sshfs by the mountinfo fstype and runs it in Normal - its held
    /// handle refreshes via ForceRead's fstat, ~2.4x faster than reopen. Restart ensures the export dir
    /// exists and the nested guest is running.
    /// </summary>
    public class VirtioFsServer : Server
    {
        public static readonly string HostIp = Lab.QemuHostIp;
        public const string HostExportDir = "/srv/ftvfs";

        private readonly ProcessRunner processRunner;

        public VirtioFsServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.VirtioFs)
        {
            this.processRunner = processRunner;
        }

        public override void Restart()
        {
            processRunner.Run("bash", $"-c 'mkdir -p {HostExportDir} && chmod 777 {HostExportDir} && (systemctl is-active --quiet ftq-guest || systemctl restart ftq-guest)'");
        }
    }
}
