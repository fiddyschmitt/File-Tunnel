using ft_tests.Runner;
using System;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for ft over an RDP <b>redirected drive</b>, with a <b>Linux</b> node acting as the RDP
    /// client. The share is not a daemon on a file server: it is the RDPDR channel of a live RDP session,
    /// so Restart() establishes that session.
    ///
    /// A Linux node runs <c>xfreerdp3</c> (the engine Remmina wraps) under <c>Xvfb</c> - no desktop
    /// environment needed, since only the redirection channel matters - and shares <see cref="ExportDir"/>
    /// into the Windows session, where it appears as <c>\\tsclient\ftshare</c>:
    ///
    ///     side1 (Linux)   -> /srv/ftrdp/x.dat           native ext4, fully coherent
    ///     side2 (Windows) -> \\tsclient\ftshare\x.dat   through RDPDR
    ///
    /// Two things make this work, both non-obvious:
    ///
    /// 1. <b>\\tsclient is session-scoped.</b> ft on Windows only sees it from inside the RDP session, so
    ///    side2 must be launched by runremote (whose server lives in the interactive session), NOT over
    ///    SSH - an SSH command runs in a different session and cannot see the redirected drive at all.
    ///
    /// 2. <b>Connecting as an existing user reconnects to that user's session</b> rather than creating a
    ///    new one (qwinsta shows rdp-tcp#N change while the session ID stays put), and runremote's server
    ///    survives the takeover. That is what puts ft and the redirected drive in the same session.
    ///
    /// Consequence: this <b>steals</b> whatever RDP client was attached to that user's session, and
    /// Windows allows only one interactive session per user, so this test cannot share a Windows node
    /// with <see cref="EndToEndTests.Rdp"/> (which needs side1's C: at \\tsclient\c). They are deliberately
    /// pointed at different boxes: Rdp uses the client2 node (.85), RdpLinux uses the server node (.84).
    ///
    /// xvfb + freerdp3-x11 are installed by provisioning on one node only
    /// (ft_test_env/Cloud/setup_debian.sh); the X stack is ~354MB and the node roots are small.
    /// </summary>
    public class RdpLinuxServer : Server
    {
        public const string ExportDir = "/srv/ftrdp";
        public const string ShareName = "ftshare";

        /// <summary>The path the same file is reached by from inside the Windows RDP session.</summary>
        public static string RedirectedPath(string filename) => $@"\\tsclient\{ShareName}\{filename}";

        private readonly ProcessRunner rdpClient;   // the Linux node that runs xfreerdp
        private readonly string windowsHostIp;
        private readonly string username;
        private readonly string password;

        public RdpLinuxServer(ProcessRunner rdpClient, string windowsHostIp, string username, string password)
            : base(OS.Windows, FileShareType.RdpLinux)
        {
            this.rdpClient = rdpClient;
            this.windowsHostIp = windowsHostIp;
            this.username = username;
            this.password = password;
        }

        public override void Restart()
        {
            // Kill by exact process NAME (-x), never -f: a full-cmdline match would also match the very
            // shell running this script (it contains the word "xfreerdp3") and kill it mid-way.
            var script =
                "pkill -9 -x xfreerdp3 2>/dev/null || true; " +
                "pkill -9 -x Xvfb 2>/dev/null || true; " +
                $"mkdir -p {ExportDir}; chmod 777 {ExportDir}; " +
                "nohup xvfb-run -a xfreerdp3 " +
                    $"/v:{windowsHostIp} /u:{username} /p:{password} /cert:ignore " +
                    $"/drive:{ShareName},{ExportDir} /size:1024x768 +auto-reconnect " +
                    ">/tmp/xfreerdp.log 2>&1 & " +
                // Block until the RDP session is actually established, otherwise ft would start against a
                // \\tsclient that does not exist yet. Checking the TCP connection to :3389 from this side
                // avoids needing to run anything inside the Windows session just to probe readiness.
                $"for i in $(seq 1 40); do ss -tn 2>/dev/null | grep -q {windowsHostIp}:3389 && break; sleep 1; done; " +
                $"ss -tn 2>/dev/null | grep -q {windowsHostIp}:3389 || echo RDP_SESSION_NOT_ESTABLISHED";

            rdpClient.Run("bash", $"-c '{script}'");
        }
    }
}
