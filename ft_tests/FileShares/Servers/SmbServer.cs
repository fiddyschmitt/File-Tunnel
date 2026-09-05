using ft_tests.Runner;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.FileShares.Servers
{
    public class SmbServer : Server
    {
        private readonly ProcessRunner processRunner;

        public SmbServer(OS OS, ProcessRunner processRunner) : base(OS, FileShareType.SMB)
        {
            this.processRunner = processRunner;
        }

        // Fired right after the server service is (re)started. A Linux `systemctl restart smbd` SEVERS every
        // client's SMB session: Linux cifs transparently auto-reconnects on next I/O, but macOS smbfs does NOT -
        // it zombies (all subsequent I/O then HANGS). So when a Mac client is involved, the test wires this hook
        // to force-remount that client AFTER the restart and BEFORE ft launches, so ft never starts against a
        // dead mount. Null (the default) for cells with no Mac client. See RefreshMacClientMount(force:true).
        public Action? AfterRestart { get; set; }

        public override void Restart()
        {
            if (OS == OS.Linux) { processRunner.Run("systemctl", "restart smbd"); AfterRestart?.Invoke(); }
            // The Windows SMB server is the dedicated server VM (.84) - a hand-built VM with a DISTINCT machine
            // SID, so the same-SID client clones can authenticate to it (Windows 24H2+ rejects same-SID SMB).
            // We do NOT restart lanmanserver per cell (as we did for the flaky externals): `net stop` PROMPTS
            // "These workstations have sessions... continue? (Y/N)" whenever a node has a live mount to
            // \\.84\Shared, and that blocks forever over the fire-and-forget runremote call (no stdin) - plus
            // it would drop every other node's mount mid-run. Instead just ensure the (persistent) Shared share
            // exists - idempotent, non-interactive, no session drop. Clear tiring by rebooting the server VM
            // (ft_test_env menu 8) when needed, not per cell.
            if (OS == OS.Windows)
                processRunner.RunCommand("if (-not (Get-SmbShare -Name Shared -ErrorAction SilentlyContinue)) { New-Item -ItemType Directory -Force 'C:\\Temp\\ft\\Shared' | Out-Null; New-SmbShare -Name Shared -Path 'C:\\Temp\\ft\\Shared' -FullAccess Everyone -ErrorAction SilentlyContinue | Out-Null }");
            // OS.Mac: intentionally a no-op. Kickstarting the Mac's smbd would drop every client's mount to
            // its share mid-test; the client-side F_NOCACHE refresh (MacDirectRefresh) handles staleness.
        }
    }
}
