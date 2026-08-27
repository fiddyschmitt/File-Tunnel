using ft_tests.Runner;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.FileShares.Servers
{
    public class NfsServer : Server
    {
        private readonly ProcessRunner processRunner;

        public NfsServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.NFS)
        {
            this.processRunner = processRunner;
        }

        public override void Restart()
        {
            // Intentionally does NOT restart nfs-server. A per-cell `systemctl restart nfs-server` drops the
            // server into its post-restart grace / not-ready window, so the client's FIRST create RPC on the
            // freshly-remounted share times out on the hard mount and retries ~26-56s - which slowed EVERY
            // NFS cell and pushed Linux-Mac Normal past the 150s connect timeout ~half the time (measured:
            // .80's first file-create 26-56s with the restart vs 20ms without). A real NFS server runs
            // continuously and is never restarted mid-use, so not restarting is both the fix and the
            // representative behaviour; the per-cell client remount (NfsClient.Restart) + cleanupFiles
            // already reset the state the test needs. This also retires the `StartLimitIntervalSec=0`
            // workaround that the rapid per-cell restarts had forced.
        }
    }
}
