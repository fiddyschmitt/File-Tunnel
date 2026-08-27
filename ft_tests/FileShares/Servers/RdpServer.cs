using ft_tests.Runner;
using System.Text;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for ft over an RDP <b>redirected drive</b>, driven by <b>mstsc</b> (the classic Rdp row). The
    /// share is not a daemon on a file server: it is the RDPDR channel of a live RDP session, so Restart()
    /// establishes that session.
    ///
    ///     side1 (.83, client) -> C:\Temp\x.dat            local NTFS, fully coherent
    ///     side2 (.84, server) -> \\tsclient\c\Temp\x.dat  through RDPDR (== side1's C:)
    ///
    /// side1 (a client clone) runs mstsc and connects to side2 (the server VM), redirecting side1's C:, which
    /// side2 then sees as <c>\\tsclient\c</c>. Two things make this work, both non-obvious:
    ///
    /// 1. <b>\\tsclient is session-scoped.</b> ft on side2 only sees it from inside the RDP session, so side2
    ///    must be launched by runremote (whose server lives in side2's interactive/autologon session), NOT
    ///    over SSH - an SSH command runs in a different session and cannot see the redirected drive at all.
    ///
    /// 2. <b>Connecting as an existing user reconnects to that user's session</b> rather than creating a new
    ///    one, and runremote's server survives the takeover - putting ft and the redirected drive in the same
    ///    session.
    ///
    /// Why side2 is the dedicated SERVER VM (.84) and not another client clone: the clients are same-SID
    /// linked clones, and Windows 24H2+ rejects RDP (and SMB) authentication between same-SID peers - so a
    /// clone cannot RDP into another clone. The server VM was built fresh, so it has a DISTINCT machine SID,
    /// and the client's RDP session to it authenticates normally. (RdpLinux points at a client, .85, instead,
    /// so the two RDP rows never fight over one box's single interactive session.)
    /// </summary>
    public class RdpServer : Server
    {
        public const string ShareName = "c";

        /// <summary>The path the same file is reached by from inside side2's RDP session.</summary>
        public static string RedirectedPath(string filename) => $@"\\tsclient\c\{filename}";

        private readonly ProcessRunner side1Node;   // the client that runs mstsc, exposing its C: as \\tsclient\c
        private readonly string targetIp;           // the server VM (receives the RDP session, runs side2's ft)
        private readonly string username;
        private readonly string password;

        public RdpServer(ProcessRunner side1Node, string targetIp, string username, string password)
            : base(OS.Windows, FileShareType.RDP)
        {
            this.side1Node = side1Node;
            this.targetIp = targetIp;
            this.username = username;
            this.password = password;
        }

        public override void Restart()
        {
            // Cache the RDP credential so mstsc does not prompt. cmdkey MUST run in the interactive session (1):
            // from an SSH/network logon it fails with "Credentials cannot be saved from this logon session", so the
            // cred never lands, mstsc then hits NLA with no credential and the RDP connect (hence \\tsclient) never
            // happens. Use Run() (runremote, session 1), NOT RunCommand() (SSH) - the same rule the SMB cmdkey follows.
            side1Node.Run("cmd.exe", $"/c cmdkey /generic:TERMSRV/{targetIp} /user:{username} /pass:{password}");

            // An .rdp that connects to side2, redirects THIS node's drives (side2 sees C: as \\tsclient\c),
            // and never prompts (cached cred + auth level 0). Written via base64 to avoid SSH quoting issues.
            var rdp = string.Join("\r\n",
                $"full address:s:{targetIp}",
                $"username:s:{username}",
                "drivestoredirect:s:*",
                "redirectdrives:i:1",
                "prompt for credentials:i:0",
                "authentication level:i:0",
                "administrative session:i:0");
            var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(rdp));
            side1Node.RunCommand("New-Item -ItemType Directory -Force C:\\Temp | Out-Null; " +
                                 $"[IO.File]::WriteAllBytes('C:\\Temp\\ft.rdp', [Convert]::FromBase64String('{b64}'))");

            // The April 2026 RDP security update (CVE-2026-26151) makes mstsc pop headless-blocking consent
            // dialogs when it OPENS an .rdp file: (a) a first-run "opening an RDP file" educational prompt;
            // (b) a per-launch resource-redirection warning that now IGNORES the .rdp's saved redirections and
            // forces the user to re-tick each resource; and (c) a "do you trust the publisher" prompt for
            // unsigned files. Defang all three the supported IT-admin way so mstsc connects unattended (none of
            // this weakens the server - it only tells THIS client to trust OUR own generated .rdp):
            //   - RdpLaunchConsentAccepted=1 (HKCU)                   -> suppresses (a)
            //   - RedirectionWarningDialogVersion=1 (HKLM ...\Client) -> reverts (b) to the pre-update behaviour,
            //     which HONOURS this .rdp's drivestoredirect (Drives already ticked) instead of re-prompting
            //   - sign ft.rdp with a self-signed code-signing cert whose SHA1 thumbprint is listed in
            //     TrustedCertThumbprints (HKLM) -> the publisher is trusted, so (c) never appears; importing the
            //     cert into LocalMachine\Root makes the self-signed chain validate.
            // All idempotent; a fresh clone gets them on its first cell. (OpenSSH gives this admin an elevated
            // token here, so the HKLM writes and LocalMachine\Root import succeed over RunCommand.)
            side1Node.RunCommand(
                "$c = Get-ChildItem Cert:\\CurrentUser\\My | Where-Object { $_.Subject -eq 'CN=ftrdpsign' } | Select-Object -First 1; " +
                "if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ftrdpsign' -CertStoreLocation Cert:\\CurrentUser\\My }; " +
                "Export-Certificate -Cert $c -FilePath C:\\Temp\\ftrdpsign.cer | Out-Null; " +
                "Import-Certificate -FilePath C:\\Temp\\ftrdpsign.cer -CertStoreLocation Cert:\\LocalMachine\\Root -ErrorAction SilentlyContinue | Out-Null; " +
                "& \"$env:SystemRoot\\system32\\rdpsign.exe\" /sha256 $c.Thumbprint C:\\Temp\\ft.rdp | Out-Null; " +
                "reg add \"HKLM\\Software\\Policies\\Microsoft\\Windows NT\\Terminal Services\" /v TrustedCertThumbprints /t REG_SZ /d $c.Thumbprint /f | Out-Null; " +
                "reg add \"HKLM\\Software\\Policies\\Microsoft\\Windows NT\\Terminal Services\\Client\" /v RedirectionWarningDialogVersion /t REG_DWORD /d 1 /f | Out-Null; " +
                "reg add \"HKCU\\Software\\Microsoft\\Terminal Server Client\" /v RdpLaunchConsentAccepted /t REG_DWORD /d 1 /f | Out-Null");

            // Kill any prior mstsc, then launch a fresh one in the INTERACTIVE session (runremote), so the
            // reconnect lands in side2's session where runremote + ft live.
            side1Node.RunCommand("taskkill /IM mstsc.exe /F");
            side1Node.Run("mstsc.exe", "C:\\Temp\\ft.rdp");

            // Block until side1 has an ESTABLISHED TCP connection to side2:3389, so ft does not start against
            // a \\tsclient that does not exist yet.
            for (var i = 0; i < 40; i++)
            {
                var (_, output) = side1Node.RunCommand($"netstat -ano -p tcp | findstr {targetIp}:3389");
                if (output.Contains($"{targetIp}:3389", StringComparison.Ordinal) &&
                    output.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                    return;
                Thread.Sleep(1000);
            }
        }
    }
}
