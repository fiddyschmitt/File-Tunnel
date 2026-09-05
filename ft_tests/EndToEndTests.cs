using CsvHelper;
using CsvHelper.Configuration;
using ft;
using ft_tests.FileShares.Clients;
using ft_tests.FileShares.Servers;
using ft_tests.Runner;
using ft_tests.Utilities;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ft_tests
{
    // These tests require the physical lab (VMs, file shares, published binaries at R:\Temp). Tag
    // them so they can be excluded from a hermetic run: `dotnet test --filter TestCategory=Unit`.
    [TestClass]
    [TestCategory("EndToEnd")]
    public class EndToEndTests
    {
        const string WIN_X64_EXE = @"R:\Temp\ft release\win-x64\ft.exe";
        const string LINUX_X64_EXE = @"R:\Temp\ft release\linux-x64\ft";
        const string OSX_ARM64_EXE = @"R:\Temp\ft release\osx-arm64\ft";

        // The Android/Termux build (issue #45): the NativeAOT linux-bionic-arm64 binary, run inside Android
        // emulators on the Mac (.33) via adb. ft_test_env launches TWO real emulators as the two Android tunnel
        // clients: emu1 (serial emulator-5556) is BRIDGED - a real LAN IP, reachable inbound - so it is client1/
        // side1; emu2 (emulator-5558) is plain NAT (outbound only) so it is client2/side2. ANDROID_ADB is the adb
        // path on the Mac. Android is a CLIENT in both positions (never a server, except the sshfs "direct" row).
        const string BIONIC_ARM64_EXE = @"R:\Temp\ft release\linux-bionic-arm64\ft";
        const string ANDROID_ADB = "/Users/smith/Library/Android/sdk/platform-tools/adb";
        const string ANDROID_SERIAL_1 = "emulator-5556";   // emu1: bridged (client1/side1)
        const string ANDROID_SERIAL_2 = "emulator-5558";   // emu2: NAT (client2/side2)

        // The Mac mounts the SMB shares in userspace under here (assumes the 'smith' login on .33).
        const string MAC_SMB_ROOT = "/Users/smith/mnt/smb";

        // The Mac mounts the .81 NFS export under here. Unlike smbfs (userspace), an NFS mount needs root,
        // so it goes through the Mac's passwordless sudo. Kept parallel to MAC_SMB_ROOT and to NfsClient's
        // Mac branch, which owns the actual (re)mount.
        const string MAC_NFS_ROOT = "/Users/smith/mnt/nfs";

        // Three dedicated Windows VMs: two CLIENT clones (.83, .85, ft_test_env-managed off ft-win-gold) plus a
        // hand-built SERVER VM (.84). The clones share the gold's machine SID, and Windows 24H2+ rejects SMB/RDP
        // auth between same-SID peers (KB-enforced CredSSP/NLA SID checks) - so the server must have a DISTINCT
        // SID: it is a fresh install, NOT cloned from ft-win-gold. The clients only ever talk to the distinct-SID
        // server (never each other over SMB/RDP), so the same-SID limitation never bites. The server is lean -
        // it needs no Guest Additions or Client-for-NFS (it never runs the VBox or NFS rows).
        const string WIN_C1_IP = "192.168.0.83";
        const string WIN_SERVER_IP = "192.168.0.84";   // hand-built server VM (distinct SID); hosts \\.84\Shared
        const string WIN_C2_IP = "192.168.0.85";

        // SOCKS end-to-end test: the dev box (this test process) is 192.168.0.31 and hosts the controlled
        // destinations the SOCKS exit dials; side1 hosts the SOCKS proxy on :5005.
        const string DEV_BOX_IP = "192.168.0.31";
        const int SOCKS_PROXY_PORT = 5005;
        const int SOCKS_HTTP_PORT = 5007;
        const int SOCKS_UDP_PORT = 5008;

        // Cross-machine SOCKS stress: side1 runs -D STRESS_A_LOCAL + -R STRESS_A_REMOTE; side2 runs
        // -D STRESS_B_LOCAL + -R STRESS_B_REMOTE → four proxies (two hosted per side). curl on each host node
        // downloads STRESS_PAYLOAD_BYTES from the dev-box server (STRESS_HTTP_PORT) through its local proxies.
        const int STRESS_A_LOCAL = 5301, STRESS_A_REMOTE = 5302, STRESS_B_LOCAL = 5303, STRESS_B_REMOTE = 5304, STRESS_HTTP_PORT = 5305;
        const int STRESS_PAYLOAD_BYTES = 32 * 1024 * 1024;

        static string localWindowsOutputFilename = "";

        static ProcessRunner win10_x64_1;   // dedicated Windows client VM .83 (client1)
        static ProcessRunner win10_x64_2;   // hand-built Windows SERVER VM .84 (distinct SID: SMB server + Rdp target)
        static ProcessRunner win10_x64_3;   // dedicated Windows client VM .85 (client2)

        // The dev box (.31) running ft LOCALLY. No longer a tunnel endpoint (that moved to the VMs); kept only
        // so VirtualBoxSharedFolder can exercise the HOST->guest path (host reads local C:\Temp, which the VM
        // guests see as \\vboxsvr\c_drive).
        static ProcessRunner devBoxLocal;


        static ProcessRunner linux_x64_1;
        static ProcessRunner linux_x64_2;
        static ProcessRunner linux_x64_3;

        static ProcessRunner mac_1;   // the Mac (.33) — side-1 ft (unique exe ft-1)
        static ProcessRunner mac_2;   // the Mac (.33) — side-2 ft (unique exe ft-2), so Mac can be on both sides

        static ProcessRunner android_1;   // Android emulator on the Mac (.33) — bionic ft, client1/side1 (instance 1, bridged)
        static ProcessRunner android_2;   // ...and client2/side2 (instance 2), so the one emulator can be on both sides (like the Mac)

        // Mac SMB client-mount credentials (set in ClassInit). Fields, not locals, so RefreshMacClientMount
        // can re-establish a Mac mount that idle-dropped - see that method and [[test-lab-mount-quirks]].
        const string macServerUser = "ftsmb";   // the service account the Mac's own smbd exposes for /ftshare
        static string macSmbUser = "";           // the .32 Windows share account (config win10_vm_username)
        static string macSmbPass = "";           // the lab smb password (config win10_vm_password)
        static string macServerPass = "";        // same lab smb password, used for the Mac ftsmb account

        // Dropbox credentials (user-secrets). When absent, the Dropbox test skips (Assert.Inconclusive)
        // rather than failing - it hits real Dropbox (no local emulator), so it is opt-in.
        static string? dropboxAppKey;
        static string? dropboxAppSecret;
        static string? dropboxRefreshToken;

        // Needed by RdpLinux: the Linux node authenticates an RDP session to the win10 VM, so the test
        // needs that box's credentials directly (not just via its ProcessRunner).
        static string? win10Username;
        static string? win10Password;

        static CsvWriter csvWriter;

        static int testNumber = 0;
        static readonly Stopwatch totalDuration = new();
        static double totalCpuUsageMs = 0;

        [ClassInitialize]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
        public static void ClassInit(TestContext context)
        {
            var config = new ConfigurationBuilder()
                                .AddUserSecrets<EndToEndTests>()
                                .Build();

            dropboxAppKey = config["dropbox_app_key"];
            dropboxAppSecret = config["dropbox_app_secret"];
            dropboxRefreshToken = config["dropbox_refresh_token"];

            win10Username = config["win10_vm_username"];
            win10Password = config["win10_vm_password"];


            var testResultsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_results");
            Directory.CreateDirectory(testResultsFolder);

            var justDateFilename = $"{DateTime.Now:yyyy-MM-dd HHmm ss}";
            var testResultsFilename = Path.Combine(testResultsFolder, $"{justDateFilename}.csv");
            localWindowsOutputFilename = Path.ChangeExtension(testResultsFilename, ".log");
            var remoteLinuxOutputFilename = $"/media/smb/192.168.0.31/r/Temp/ft release/linux-x64/output/{justDateFilename}";

            // The runners redirect each ft's output into an 'output' folder beside its binary
            // (the Linux nodes reach it via the dev box's 'r' share). These folders must exist or
            // the redirect fails and ft never launches, so ensure them up front. R: is local to
            // the dev box, so creating them here also makes them visible to the nodes over CIFS.
            foreach (var exePath in new[] { WIN_X64_EXE, LINUX_X64_EXE })
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exePath)!, "output"));
            }

            // The dev box (.31) as a LOCAL ft host - only for VirtualBoxSharedFolder's host->guest rows.
            devBoxLocal = new LocalWindowsProcessRunner(WIN_X64_EXE, localWindowsOutputFilename);

            // Three Windows VMs, all reached via runremote: clients .83/.85 (same-SID clones off ft-win-gold)
            // and the server VM .84 (distinct SID, hand-built). The clients share the baked-in account
            // (win10_vm_*); the server has the same username/password but a distinct SID, so SMB/RDP pass-through
            // from the clients works. A node can be down (rebooting to clear tiring, or not yet brought up), so
            // tolerate it: a row that needs the missing node is skipped (Assert.Inconclusive), the rest still run.
            try { win10_x64_1 = new RemoteWindowsProcessRunner(WIN_C1_IP, config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE); }
            catch (Exception ex) { Console.WriteLine($"WARN: win10_x64_1 ({WIN_C1_IP}) unavailable: {ex.Message}"); win10_x64_1 = null!; }
            try { win10_x64_2 = new RemoteWindowsProcessRunner(WIN_SERVER_IP, config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE); }
            catch (Exception ex) { Console.WriteLine($"WARN: win10_x64_2 ({WIN_SERVER_IP}) unavailable: {ex.Message}"); win10_x64_2 = null!; }
            try { win10_x64_3 = new RemoteWindowsProcessRunner(WIN_C2_IP, config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE); }
            catch (Exception ex) { Console.WriteLine($"WARN: win10_x64_3 ({WIN_C2_IP}) unavailable: {ex.Message}"); win10_x64_3 = null!; }

            linux_x64_1 = new LinuxProcessRunner("192.168.0.80", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.80.log");
            linux_x64_2 = new LinuxProcessRunner("192.168.0.81", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.81.log");
            linux_x64_3 = new LinuxProcessRunner("192.168.0.82", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.82.log");

            // The Mac (.33): SSH key auth, ft in userspace. Not orchestrator-managed, so its SMB client
            // mounts are ensured here rather than by Linux provisioning. Two runner instances (ft-1/ft-2)
            // let the Mac be on both tunnel sides at once. macOS Normal-mode SMB works via MacDirectRefresh
            // (ForceRead's separate F_NOCACHE read), so both modes are covered.
            var macUser = config["mac_ssh_username"] ?? "smith";
            var macKey = config["mac_ssh_keypath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
            mac_1 = new MacProcessRunner("192.168.0.33", macUser, macKey, OSX_ARM64_EXE, "/tmp/ft-mac.log", instance: 1);
            mac_2 = new MacProcessRunner("192.168.0.33", macUser, macKey, OSX_ARM64_EXE, "/tmp/ft-mac2.log", instance: 2);

            // The two Android emulators on the Mac (bionic ft over adb). Only up when ft_test_env has launched them,
            // so tolerate absence: a null runner makes the Android rows skip (Assert.Inconclusive). TWO REAL emulators
            // (not two instances on one): android_1 = emu1 emulator-5556 (bridged, reachable) = client1/side1;
            // android_2 = emu2 emulator-5558 (NAT, outbound) = client2/side2. So Android is a genuine distinct device
            // on each tunnel side (incl. Android-Android).
            try { android_1 = new AndroidProcessRunner("192.168.0.33", macUser, macKey, BIONIC_ARM64_EXE, ANDROID_ADB, ANDROID_SERIAL_1, instance: 1); }
            catch (Exception ex) { Console.WriteLine($"WARN: android_1 (emulator-5556) unavailable: {ex.Message}"); android_1 = null!; }
            try { android_2 = new AndroidProcessRunner("192.168.0.33", macUser, macKey, BIONIC_ARM64_EXE, ANDROID_ADB, ANDROID_SERIAL_2, instance: 2); }
            catch (Exception ex) { Console.WriteLine($"WARN: android_2 (emulator-5558) unavailable: {ex.Message}"); android_2 = null!; }
            // Client mounts of the remote SMB servers (idempotent, as the user - no sudo). See
            // RefreshMacClientMount for the mount details and why these are re-established per cell.
            macSmbUser = config["win10_vm_username"] ?? "";
            macSmbPass = config["win10_vm_password"] ?? "";
            RefreshMacClientMount(OS.Linux);     // the .81 Samba share
            RefreshMacClientMount(OS.Windows);   // the .32 Windows share

            // The Mac as an SMB SERVER (.33 hosts /Users/smith/ftshare, share 'ftshare') so it can be the
            // server for other nodes too. Enabled via passwordless sudo. macOS does NOT put the SMB-NT hash
            // in a new account's HASHLIST, so SMB auth fails until we add it and re-set the password (dscl
            // works for this non-secure-token service account; sysadminctl needs a secure-token unlock). The
            // setup is streamed base64-encoded and run through `sudo bash` to dodge C#->SSH->zsh quoting.
            macServerPass = config["win10_vm_password"] ?? "";   // reuse the lab smb password for the ftsmb account
            var macServerSetup = string.Join('\n',
                "launchctl enable system/com.apple.smbd 2>/dev/null",
                "launchctl bootstrap system /System/Library/LaunchDaemons/com.apple.smbd.plist 2>/dev/null || true",
                "mkdir -p /Users/smith/ftshare && chmod 777 /Users/smith/ftshare",
                "sharing -l | grep -q 'name:.*ftshare' || sharing -a /Users/smith/ftshare -S ftshare -s 001 -g 000",
                $"id {macServerUser} >/dev/null 2>&1 || sysadminctl -addUser {macServerUser} -fullName 'FT SMB' -password '{macServerPass}' >/dev/null 2>&1",
                $"dscl . -create /Users/{macServerUser} AuthenticationAuthority ';ShadowHash;HASHLIST:<SALTED-SHA512-PBKDF2,SMB-NT>'",
                $"dscl . -passwd /Users/{macServerUser} '{macServerPass}'");
            mac_1.RunCommand($"echo {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(macServerSetup))} | base64 -d | sudo bash");
            // Mac loopback mount of its own share, so the Mac can be a client of the Mac server (both sides).
            RefreshMacClientMount(OS.Mac);
            RefreshMacShareClientNodeMounts();

            var writer = new StreamWriter(testResultsFilename)
            {
                AutoFlush = true
            };
            csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
            });

            csvWriter.WriteField("test_num");

            csvWriter.WriteField("result");
            csvWriter.WriteField("duration");


            csvWriter.WriteField("file_share_type");
            csvWriter.WriteField("mode");
            csvWriter.WriteField("client_1");
            csvWriter.WriteField("server");
            csvWriter.WriteField("client_2");

            csvWriter.WriteField("total_processor_time_ms_1");
            csvWriter.WriteField("total_processor_time_ms_2");

            csvWriter.WriteField("command_1");
            csvWriter.WriteField("command_2");

            csvWriter.WriteField("error_message");

            csvWriter.Flush();

            totalDuration.Start();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            totalDuration.Stop();

            csvWriter.NextRecord();

            csvWriter.WriteField("");
            csvWriter.WriteField("");

            csvWriter.WriteField($"{totalDuration.Elapsed.TotalSeconds:0.000}");

            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");

            csvWriter.WriteField($"{totalCpuUsageMs.ToString("0", CultureInfo.InvariantCulture)}");

            csvWriter.Flush();
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Linux, Mode.Normal)]
        // Mac as an SMB client, over the .81 Samba share. Both modes work. macOS's SMB client caches
        // aggressively - a held-handle read is stale even across a reopen (the smbfs attribute cache
        // survives it) - so both modes defeat the cache with F_NOCACHE: IsolatedIo reopens per read
        // (IsolatedReadsFileStream), and Normal's ForceRead reads the awaited page through a separate
        // F_NOCACHE handle (MacDirectRefresh), refreshing the held view - the macOS analog of the Linux
        // O_DIRECT refresh. Both tunnel directions covered.
        [DataRow(OS.Mac, OS.Linux, OS.Linux, Mode.Normal, DisplayName = "Smb Mac-Linux-Linux Normal")]
        [DataRow(OS.Linux, OS.Linux, OS.Mac, Mode.Normal, DisplayName = "Smb Linux-Linux-Mac Normal")]
        // Mac client against Windows (.32) and Linux (.81) servers, paired with every other-client OS
        // (Windows/Linux/Mac). The two-Mac rows exercise ft-1 + ft-2 coexisting on the single Mac.
        [DataRow(OS.Mac, OS.Windows, OS.Windows, Mode.Normal, DisplayName = "Smb Mac-Windows-Windows Normal")]
        [DataRow(OS.Mac, OS.Windows, OS.Linux, Mode.Normal, DisplayName = "Smb Mac-Windows-Linux Normal")]
        [DataRow(OS.Mac, OS.Windows, OS.Mac, Mode.Normal, DisplayName = "Smb Mac-Windows-Mac Normal")]
        [DataRow(OS.Mac, OS.Linux, OS.Windows, Mode.Normal, DisplayName = "Smb Mac-Linux-Windows Normal")]
        [DataRow(OS.Mac, OS.Linux, OS.Mac, Mode.Normal, DisplayName = "Smb Mac-Linux-Mac Normal")]
        [DataRow(OS.Windows, OS.Windows, OS.Mac, Mode.Normal, DisplayName = "Smb Windows-Windows-Mac Normal")]
        [DataRow(OS.Windows, OS.Linux, OS.Mac, Mode.Normal, DisplayName = "Smb Windows-Linux-Mac Normal")]
        [DataRow(OS.Linux, OS.Windows, OS.Mac, Mode.Normal, DisplayName = "Smb Linux-Windows-Mac Normal")]
        // Mac as the SMB SERVER (.33 /ftshare), every client pairing (Windows/Linux/Mac), both modes.
        [DataRow(OS.Mac, OS.Mac, OS.Mac, Mode.Normal, DisplayName = "Smb Mac-Mac-Mac Normal")]
        [DataRow(OS.Mac, OS.Mac, OS.Linux, Mode.Normal, DisplayName = "Smb Mac-Mac-Linux Normal")]
        [DataRow(OS.Linux, OS.Mac, OS.Mac, Mode.Normal, DisplayName = "Smb Linux-Mac-Mac Normal")]
        [DataRow(OS.Linux, OS.Mac, OS.Linux, Mode.Normal, DisplayName = "Smb Linux-Mac-Linux Normal")]
        [DataRow(OS.Windows, OS.Mac, OS.Mac, Mode.Normal, DisplayName = "Smb Windows-Mac-Mac Normal")]
        [DataRow(OS.Windows, OS.Mac, OS.Linux, Mode.Normal, DisplayName = "Smb Windows-Mac-Linux Normal")]
        [DataRow(OS.Mac, OS.Mac, OS.Windows, Mode.Normal, DisplayName = "Smb Mac-Mac-Windows Normal")]
        [DataRow(OS.Linux, OS.Mac, OS.Windows, Mode.Normal, DisplayName = "Smb Linux-Mac-Windows Normal")]
        [DataRow(OS.Windows, OS.Mac, OS.Windows, Mode.Normal, DisplayName = "Smb Windows-Mac-Windows Normal")]
        public void Smb(OS client1OS, OS serverOS, OS client2OS, Mode mode)
        {
            SmbServer smbServer = serverOS switch
            {
                OS.Linux => new SmbServer(OS.Linux, linux_x64_2),
                OS.Mac => new SmbServer(OS.Mac, mac_1),
                _ => new SmbServer(OS.Windows, win10_x64_2)
            };

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SmbPathLookup(client1OS, serverOS, filename1);
            var readPath1 = SmbPathLookup(client1OS, serverOS, filename2);
            var client1Runner = client1OS switch { OS.Windows => win10_x64_1, OS.Mac => mac_1, _ => linux_x64_1 };
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = SmbPathLookup(client2OS, serverOS, filename1);
            var writePath2 = SmbPathLookup(client2OS, serverOS, filename2);
            var client2Runner = client2OS switch { OS.Windows => win10_x64_3, OS.Mac => mac_2, _ => linux_x64_3 };
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            // A required lab node may be down (see the ClassInit tolerance). Skip rather than NRE-fail, so
            // one dead node doesn't fail rows across the matrix; the row runs once the node is back.
            if (client1Runner is null || client2Runner is null || (serverOS == OS.Windows && win10_x64_2 is null))
                Assert.Inconclusive($"Skipped: a required lab node is unavailable ({client1OS}-{serverOS}-{client2OS} {mode}).");

            // Client mounts idle-drop during the preceding (non-Mac) cells - which run first, so by the time a
            // Mac-involved cell runs its mounts have sat idle for many minutes. Refresh both clients' mounts to
            // the server right before the cell.
            if (client1OS == OS.Mac || client2OS == OS.Mac || serverOS == OS.Mac)
            {
                RefreshSmbClientMount(client1OS, serverOS, client1Runner);
                RefreshSmbClientMount(client2OS, serverOS, client2Runner);
            }

            // ...but the per-cell `systemctl restart smbd` (server.Restart, run right before ft launches) then
            // SEVERS the freshly-established session. Linux cifs auto-reconnects; macOS smbfs does NOT - it
            // zombies and every ft I/O HANGS ("Could not enqueue Ping" -> "Could not connect", deterministic
            // 5/5). So for a Mac client of the Linux Samba server, re-mount AFTER the restart via this hook,
            // which server.Restart fires once smbd is back (validated: remount succeeds ~1-2.5s post-restart).
            if (serverOS == OS.Linux && (client1OS == OS.Mac || client2OS == OS.Mac))
                smbServer.AfterRestart = () => RefreshMacClientMount(OS.Linux, force: true);

            // A Windows client reaches a non-Windows SMB server (.81 Samba / .33 macOS) via a cmdkey that must
            // live in ft's interactive session - seed it there before the cell (idempotent; see the helper).
            EnsureWinClientSessionCred(client1OS, serverOS, client1Runner);
            EnsureWinClientSessionCred(client2OS, serverOS, client2Runner);

            ConductTunnelTests(mode, side1, smbServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        // (Re)establish the Mac's smbfs client mount to the given server's share, idempotently and
        // authenticated. macOS drops an idle SMB session, so a Mac client mount set up in ClassInit is
        // often dead by the time a Mac cell runs after the ~15min of non-Mac cells - surfacing as "Could
        // not connect" (the Mac ft can't write its tunnel file; the log shows "Could not enqueue Ping"
        // within 2s). This runs in ClassInit AND before each Mac SMB cell to catch that idle drop; the Linux
        // server's per-cell `systemctl restart smbd` ALSO severs the session, so it is re-run post-restart via
        // SmbServer.AfterRestart with force:true. Authenticated throughout - the typical option, and a guest
        // session idle-drops fastest (~90s vs 200s+). A dead mount can VANISH from `mount` OR ZOMBIE (still
        // listed but I/O HANGS); the poll-bounded probe below handles both. No-op if mac_1 is null.
        private static void RefreshMacClientMount(OS serverOS, bool force = false)
        {
            if (mac_1 == null) return;
            var (mp, mountSrc) = serverOS switch
            {
                OS.Linux => ($"{MAC_SMB_ROOT}/192.168.0.81/data", "//user:live@192.168.0.81/data"),
                OS.Windows => ($"{MAC_SMB_ROOT}/{WIN_SERVER_IP}/shared", $"//{macSmbUser}:{macSmbPass}@{WIN_SERVER_IP}/Shared"),
                _ => ($"{MAC_SMB_ROOT}/192.168.0.33/ftshare", $"//{macServerUser}:{macServerPass}@192.168.0.33/ftshare"),
            };
            if (force)
            {
                // The caller KNOWS the mount is dead - e.g. a server `systemctl restart smbd` just severed the
                // smbfs session, which macOS does NOT auto-reconnect (it zombies). Skip the writability probe
                // (which would only waste 8s hanging on the dead mount) and remount directly. Validated on .33:
                // restart-then-remount succeeds reliably in ~1-2.5s.
                mac_1.RunCommand($"MP=\"{mp}\"; mkdir -p \"$MP\"; umount -f \"$MP\" 2>/dev/null; mount_smbfs \"{mountSrc}\" \"$MP\"");
                return;
            }
            // An idle-dropped smbfs mount can VANISH from `mount` OR ZOMBIE (still listed but dead). The trap:
            // a ZOMBIE smbfs mount BLOCKS I/O instead of failing - a bare `touch` HANGS forever. The old
            // `if mount|grep && touch ... else remount` therefore hung on the touch and never reached the
            // remount branch; the bounded SSH just timed out and left the DEAD mount in place, so the
            // late-ordered Mac SMB cells failed 5/5 with "Could not connect" (ft can't even write its Ping).
            // Fix: probe writability in the BACKGROUND with its fds redirected (so a hung probe can't hold the
            // SSH channel open) and poll it for up to 8s via `kill -0`; a probe that hangs (or fails, or a
            // vanished mount) => force-remount. A healthy mount passes in ~1s and is left untouched. Validated
            // on .33: healthy 1.1s, unmounted 1.2s->remount, hung-probe 8.5s->remount.
            mac_1.RunCommand($"MP=\"{mp}\"; mkdir -p \"$MP\"; ok=0; M=\"/tmp/.ftw.$$\"; rm -f \"$M\"; " +
                $"if mount | grep -q \"$MP\"; then " +
                    $"( touch \"$MP/.ftw\" 2>/dev/null && rm -f \"$MP/.ftw\" 2>/dev/null && echo 1 > \"$M\" ) >/dev/null 2>&1 & p=$!; " +
                    $"i=0; while [ $i -lt 8 ]; do kill -0 $p 2>/dev/null || break; sleep 1; i=$((i+1)); done; " +
                    $"kill -9 $p 2>/dev/null; " +
                    $"[ \"$(cat \"$M\" 2>/dev/null)\" = 1 ] && ok=1; " +
                $"fi; rm -f \"$M\"; " +
                $"[ $ok -eq 1 ] || {{ umount -f \"$MP\" 2>/dev/null; mount_smbfs \"{mountSrc}\" \"$MP\"; }}");
        }

        // (Re)establish ONE SMB client's *mount* to the given server's share, idempotently: Mac via smbfs
        // (RefreshMacClientMount), Linux via cifs. A Windows client needs no mount - only a session-1 cmdkey,
        // handled separately by EnsureWinClientSessionCred (a cmdkey has to be saved from ft's interactive
        // session, which this SSH-based path can't reach). Called before each Mac-involved cell because these
        // mounts idle-drop over the long run before the (last-ordered) Mac cells - the per-cell server-service
        // restart (SmbServer.Restart) doesn't touch the client mount. The Linux cifs mount idle-ZOMBIES
        // (mountpoint -q passes but a write fails - see [[test-lab-mount-quirks]]), so test writability as root
        // and force-remount (umount -l + mount) on failure. No-op if the runner is null or the client is Windows.
        private static void RefreshSmbClientMount(OS clientOS, OS serverOS, ProcessRunner? runner)
        {
            if (runner == null) return;
            if (clientOS == OS.Mac) { RefreshMacClientMount(serverOS); return; }
            if (clientOS == OS.Linux)
            {
                var (mp, src, user, pass) = serverOS switch
                {
                    OS.Linux => ("/media/smb/192.168.0.81/data", "//192.168.0.81/data", "user", "live"),
                    OS.Windows => ($"/media/smb/{WIN_SERVER_IP}/shared", $"//{WIN_SERVER_IP}/Shared", macSmbUser, macSmbPass),
                    _ => ("/media/smb/192.168.0.33/ftshare", "//192.168.0.33/ftshare", macServerUser, macServerPass),
                };
                runner.RunCommand($"sudo sh -c 'MP={mp}; mkdir -p $MP; if mountpoint -q $MP && touch $MP/.ftw 2>/dev/null; then rm -f $MP/.ftw; else umount -l $MP 2>/dev/null; mount -t cifs {src} $MP -o username={user},password={pass},vers=3.0; fi'");
            }
        }

        // A Windows client authenticates to a NON-Windows SMB server (Samba .81 or macOS .33) with a stored
        // credential, and that credential MUST be created in the INTERACTIVE session (1) where ft runs: cmdkey
        // saved from the SSH session-0 logon lands in a different logon and is invisible to ft (SSH reports
        // "cannot save from this logon session"). runremote's Run() launches cmdkey in session 1, exactly where
        // ft launches - proven: with the cred seeded this way (and .81 Samba signing enabled) ft opens the share.
        // Idempotent and cheap; a no-op for a Windows server (the client authenticates to .84 with its own
        // matching smith account) or a non-Windows client. Called before EVERY Smb cell - the .81 rows never
        // trigger the Mac mount refresh, so this is their only credential seed.
        private static void EnsureWinClientSessionCred(OS clientOS, OS serverOS, ProcessRunner? runner)
        {
            if (runner == null || clientOS != OS.Windows || serverOS == OS.Windows) return;
            var (host, user, pass) = serverOS == OS.Linux
                ? ("192.168.0.81", "user", "live")
                : ("192.168.0.33", macServerUser, macServerPass);
            runner.Run("cmd.exe", $"/c cmdkey /add:{host} /user:{user} /pass:{pass}");
        }

        // Initial ClassInit setup of the Linux client-node mounts of the Mac's own .33 share. (Windows clients
        // need no mount to .33 - just a session-1 cmdkey, which EnsureWinClientSessionCred seeds per cell.)
        private static void RefreshMacShareClientNodeMounts()
        {
            foreach (var lin in new[] { linux_x64_1, linux_x64_3 }) RefreshSmbClientMount(OS.Linux, OS.Mac, lin);
        }

        private static string SmbPathLookup(OS client, OS server, string fileName)
        {
            var clientSep = client == OS.Windows ? '\\' : '/';
            var otherSep = client == OS.Windows ? '/' : '\\';
            fileName = fileName.Replace(otherSep, clientSep).TrimStart('\\', '/');

            string basePath = (client, server) switch
            {
                (OS.Windows, OS.Windows) => @$"\\{WIN_SERVER_IP}\shared\",
                (OS.Windows, OS.Linux) => @$"\\192.168.0.81\data\",
                (OS.Linux, OS.Windows) => @$"/media/smb/{WIN_SERVER_IP}/shared/",
                (OS.Linux, OS.Linux) => @$"/media/smb/192.168.0.81/data/",
                (OS.Mac, OS.Linux) => $@"{MAC_SMB_ROOT}/192.168.0.81/data/",
                (OS.Mac, OS.Windows) => $@"{MAC_SMB_ROOT}/{WIN_SERVER_IP}/shared/",
                (OS.Mac, OS.Mac) => $@"{MAC_SMB_ROOT}/192.168.0.33/ftshare/",
                (OS.Linux, OS.Mac) => @$"/media/smb/192.168.0.33/ftshare/",
                (OS.Windows, OS.Mac) => @$"\\192.168.0.33\ftshare\",
                _ => throw new InvalidOperationException("Unsupported client/server OS combo")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal, DisplayName = "Nfs Windows-Linux-Windows Normal")]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedIo, DisplayName = "Nfs Windows-Linux-Windows IsolatedIo")]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal, DisplayName = "Nfs Windows-Linux-Linux Normal")]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedIo, DisplayName = "Nfs Windows-Linux-Linux IsolatedIo")]
        [DataRow(OS.Linux, OS.Windows, Mode.Normal, DisplayName = "Nfs Linux-Linux-Windows Normal")]
        [DataRow(OS.Linux, OS.Windows, Mode.IsolatedIo, DisplayName = "Nfs Linux-Linux-Windows IsolatedIo")]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal, DisplayName = "Nfs Linux-Linux-Linux Normal")]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedIo, DisplayName = "Nfs Linux-Linux-Linux IsolatedIo")]
        // The Mac (.33) as an NFS client of the .81 export (macOS mounts it via sudo + resvport; ft's
        // MacDirectRefresh handles read coherency). Completes the client1 x client2 matrix over {W,L,M}.
        [DataRow(OS.Mac, OS.Linux, Mode.Normal, DisplayName = "Nfs Mac-Linux-Linux Normal")]
        [DataRow(OS.Mac, OS.Linux, Mode.IsolatedIo, DisplayName = "Nfs Mac-Linux-Linux IsolatedIo")]
        [DataRow(OS.Linux, OS.Mac, Mode.Normal, DisplayName = "Nfs Linux-Linux-Mac Normal")]
        [DataRow(OS.Linux, OS.Mac, Mode.IsolatedIo, DisplayName = "Nfs Linux-Linux-Mac IsolatedIo")]
        [DataRow(OS.Mac, OS.Windows, Mode.Normal, DisplayName = "Nfs Mac-Linux-Windows Normal")]
        [DataRow(OS.Mac, OS.Windows, Mode.IsolatedIo, DisplayName = "Nfs Mac-Linux-Windows IsolatedIo")]
        [DataRow(OS.Windows, OS.Mac, Mode.Normal, DisplayName = "Nfs Windows-Linux-Mac Normal")]
        [DataRow(OS.Windows, OS.Mac, Mode.IsolatedIo, DisplayName = "Nfs Windows-Linux-Mac IsolatedIo")]
        [DataRow(OS.Mac, OS.Mac, Mode.Normal, DisplayName = "Nfs Mac-Linux-Mac Normal")]
        [DataRow(OS.Mac, OS.Mac, Mode.IsolatedIo, DisplayName = "Nfs Mac-Linux-Mac IsolatedIo")]
        public void Nfs(OS client1OS, OS client2OS, Mode mode)
        {
            var nfsServer = new NfsServer(linux_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = NfsPathLookup(client1OS, filename1);
            var readPath1 = NfsPathLookup(client1OS, filename2);
            var client1Runner = client1OS switch { OS.Windows => win10_x64_1, OS.Mac => mac_1, _ => linux_x64_1 };
            var side1 = new NfsClient(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = NfsPathLookup(client2OS, filename1);
            var writePath2 = NfsPathLookup(client2OS, filename2);
            var client2Runner = client2OS switch { OS.Windows => win10_x64_3, OS.Mac => mac_2, _ => linux_x64_3 };
            var side2 = new NfsClient(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            // A Windows client node may be down (those are null-tolerant in ClassInit). Skip rather than
            // NRE-fail so one dead node doesn't fail rows across the matrix.
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive($"Skipped: a required lab node is unavailable (NFS {client1OS}-{client2OS} {mode}).");

            ConductTunnelTests(mode, side1, nfsServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string NfsPathLookup(OS client, string fileName)
        {
            var clientSep = client == OS.Windows ? '\\' : '/';
            var otherSep = client == OS.Windows ? '/' : '\\';
            fileName = fileName.Replace(otherSep, clientSep).TrimStart('\\', '/');

            string basePath = client switch
            {
                OS.Windows => @"X:\",
                OS.Linux => "/media/nfs/192.168.0.81/tmpfs/",
                OS.Mac => $"{MAC_NFS_ROOT}/192.168.0.81/tmpfs/",
                _ => throw new InvalidOperationException("Unsupported client OS")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        // sshfs (a FUSE filesystem over SSH), client1 - server - client2, mirroring the NFS topology: both clients
        // sshfs-mount the same export on the server and see each other's writes through it. The clients are the
        // FUSE-capable platforms - Linux (.80/.82) and Android (issue #45; the emulator runs the real Termux sshfs
        // toolchain, so bionic ft reads/writes a genuine FUSE mount, statfs f_type 0x65735546, and auto-enables
        // IsolatedIo just like Linux). Android is just another client permutation here, not a special case.
        // The SERVER axis is the full {Windows, Linux, Mac} set: Windows has no native sshfs and this lab's Mac has
        // no macFUSE, so neither can be an sshfs CLIENT, but any OS can be the sshfs SERVER (it just runs sshd). The
        // mount point is per-client (identical on the two Linux nodes, per-instance on the shared emulator), so the
        // path PREFIX is per-client while the underlying server file is shared.
        public static IEnumerable<object[]> SshfsMatrixCombos =>
            from c1 in new[] { OS.Linux, OS.Android }
            from c2 in new[] { OS.Linux, OS.Android }
            from serverOS in new[] { OS.Windows, OS.Linux, OS.Mac }
            from mode in new[] { Mode.Normal, Mode.IsolatedIo }
            select new object[] { c1, c2, serverOS, mode };

        // "Sshfs {client1}-{server}-{client2} {mode}", e.g. "Sshfs Android-Linux-Linux Normal".
        public static string SshfsRow(System.Reflection.MethodInfo methodInfo, object[] data)
            => $"Sshfs {data[0]}-{data[2]}-{data[1]} {data[3]}";

        [DataTestMethod]
        [DynamicData(nameof(SshfsMatrixCombos), DynamicDataDisplayName = nameof(SshfsRow))]
        public void Sshfs(OS client1OS, OS client2OS, OS serverOS, Mode mode)
        {
            var (server, spec, _, _) = MakeSshfsServer(serverOS);
            if (server is null || spec is null)
                Assert.Inconclusive($"Skipped: sshfs server node {serverOS} unavailable.");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SshfsPath(client1OS, 1, filename1);
            var readPath1 = SshfsPath(client1OS, 1, filename2);
            var side1 = MakeSshfsClient(client1OS, 1, spec, $"-w {writePath1} -r {readPath1} --verbose"); // .80 / emulator

            var readPath2 = SshfsPath(client2OS, 2, filename1);
            var writePath2 = SshfsPath(client2OS, 2, filename2);
            var side2 = MakeSshfsClient(client2OS, 2, spec, $"-r {readPath2} -w {writePath2} --verbose"); // .82 / emulator

            if (side1 is null || side2 is null)
                Assert.Inconclusive($"Skipped: a required client node is unavailable (Sshfs {client1OS}-{serverOS}-{client2OS}).");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        // Resolve a shared server file to its absolute path through THIS client's own sshfs mount point.
        private static string SshfsPath(OS os, int side, string fileName)
        {
            var mount = os == OS.Android ? AndroidSshfsClient.MountPoint(side) : SshfsClient.MountPoint;
            return $"{mount}/{fileName.TrimStart('/')}";
        }

        // The right sshfs client for the OS, mounting the server named by the spec: a Linux node (.80 side1 / .82
        // side2), or the emulator's two ft instances. Returns null when the needed node is down, so the row self-skips.
        private static Client? MakeSshfsClient(OS os, int side, SshfsMountSpec spec, string args)
        {
            if (os == OS.Android)
            {
                var r = side == 1 ? android_1 : android_2;
                return r is null ? null : new AndroidSshfsClient((AndroidProcessRunner)r, side, spec, args);
            }
            var lr = side == 1 ? linux_x64_1 : linux_x64_3;
            return lr is null ? null : new SshfsClient(lr, spec, args);
        }

        // The .81 Linux sshfs server spec (user/live over its standard sshd) - the password baseline.
        static readonly SshfsMountSpec LinuxSshfsSpec = new(SshfsServer.ServerIp, 22, "user", SshfsServer.ExportDir, "live", null);

        // A throwaway ed25519 keypair for the key-auth sshfs servers (the Android-direct emu1 sshd; the Mac). Made
        // once on the dev box with ssh-keygen; the public text is authorized in each server, the private key is
        // pushed to the client emulator. NOT the user's personal key.
        static string? _sshfsKeyPriv;
        static string? _sshfsKeyPub;
        static (string privPath, string pubText) LabSshfsKey()
        {
            if (_sshfsKeyPriv != null) return (_sshfsKeyPriv, _sshfsKeyPub!);
            var dir = Path.Combine(Path.GetTempPath(), "ft_sshfs_key");
            Directory.CreateDirectory(dir);
            var priv = Path.Combine(dir, "id_ed25519");
            if (!File.Exists(priv))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("ssh-keygen", $"-t ed25519 -N \"\" -f \"{priv}\" -q")
                { UseShellExecute = false, CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi)!.WaitForExit();
            }
            _sshfsKeyPriv = priv;
            _sshfsKeyPub = File.ReadAllText(priv + ".pub").Trim();
            return (priv, _sshfsKeyPub);
        }

        // The "direct" sshfs rows (issue #45): Android SSHs into a target that IS the other tunnel endpoint - the
        // target reads/writes the shared export on its OWN local fs, so there is no third server node. "One Android
        // SSHs into X, who writes to their local fs", for X in {Android, Windows, Linux, Mac}. side1 is always emu1
        // (bridged, reachable inbound from .31 - which ConductTunnelTests requires of side1). For X in {Linux, Mac,
        // Windows} emu1 mounts the LAN target and the target node is side2 (reads its export locally). For X = Android
        // the target is emu2, which is NAT (unreachable inbound) and so must be the MOUNTING side: emu1 hosts Termux
        // sshd + reads its export locally (server + side1) and emu2 mounts emu1 (side2). Either way it is an Android
        // SSHing into an Android. Key/password auth per target; ft auto-enables IsolatedIo over the FUSE mount.
        public static IEnumerable<object[]> SshfsDirectCombos =>
            from target in new[] { OS.Android, OS.Windows, OS.Linux, OS.Mac }
            from mode in new[] { Mode.Normal, Mode.IsolatedIo }
            // The Windows direct target is IsolatedIo-only. It reads its export on its own LOCAL disk, whose
            // coherent fs auto-detects to Normal mode (held handles) - but the counterpart reaches the same
            // files through Windows's OWN OpenSSH sftp server, which opens files exclusively, so a held local
            // handle blocks the counterpart's sftp access. Only --isolated-io (no held handle) works, and ft
            // cannot auto-select it: it has no way to know a local file is also served over an exclusive-open
            // sftp server. (Windows as a plain sshfs SERVER in the matrix works in both modes - there the
            // counterparts reach it only over sftp, never locally.)
            where !(target == OS.Windows && mode == Mode.Normal)
            select new object[] { target, mode };

        // "Sshfs Android-{target} direct {mode}", e.g. "Sshfs Android-Linux direct Normal".
        public static string SshfsDirectRow(System.Reflection.MethodInfo methodInfo, object[] data)
            => $"Sshfs Android-{data[0]} direct {data[1]}";

        [DataTestMethod]
        [DynamicData(nameof(SshfsDirectCombos), DynamicDataDisplayName = nameof(SshfsDirectRow))]
        public void SshfsDirect(OS targetOS, Mode mode)
        {
            if (android_1 is null)
                Assert.Inconclusive("Skipped: the sshfs-direct rows need emu1 (the Android client).");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            if (targetOS == OS.Android)
            {
                // emu1 = server + side1 (reads its export LOCALLY); emu2 (NAT) mounts emu1 = side2. Key auth, port 8022.
                if (android_2 is null)
                    Assert.Inconclusive("Skipped: Android-Android direct needs BOTH emulators.");
                var (privKey, pubText) = LabSshfsKey();
                var androidServer = new AndroidSshfsServer((AndroidProcessRunner)android_1, pubText);
                var host = androidServer.Host;
                if (host is null)
                    Assert.Inconclusive("Skipped: emu1 (sshfs-direct server) has no bridged LAN IP.");

                var aw1 = $"{AndroidSshfsServer.ExportDir}/{filename1}";
                var ar1 = $"{AndroidSshfsServer.ExportDir}/{filename2}";
                var as1 = new Client(OS.Android, android_1, $"-w {aw1} -r {ar1} --verbose");

                var androidSpec = new SshfsMountSpec(host, AndroidSshfsServer.SshdPort, AndroidSshfsServer.SshUser, AndroidSshfsServer.ExportDir, null, privKey);
                var ar2 = $"{AndroidSshfsClient.MountPoint(2)}/{filename1}";
                var aw2 = $"{AndroidSshfsClient.MountPoint(2)}/{filename2}";
                var as2 = new AndroidSshfsClient((AndroidProcessRunner)android_2, 2, androidSpec, $"-r {ar2} -w {aw2} --verbose");

                ConductTunnelTests(mode, as1, androidServer, as2, ar1, aw1, ar2, aw2);
                return;
            }

            // target in {Linux, Mac, Windows}: emu1 (side1) sshfs-mounts the LAN target; the target node reads its
            // export LOCALLY (side2). Only one mount (emu1); the target accesses the same files directly on disk.
            var (server, spec, targetRunner, localExport) = MakeSshfsServer(targetOS);
            if (server is null || spec is null || targetRunner is null || localExport is null)
                Assert.Inconclusive($"Skipped: sshfs-direct target {targetOS} unavailable.");

            var writePath1 = $"{AndroidSshfsClient.MountPoint(1)}/{filename1}";
            var readPath1 = $"{AndroidSshfsClient.MountPoint(1)}/{filename2}";
            var side1 = new AndroidSshfsClient((AndroidProcessRunner)android_1, 1, spec, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = $"{localExport}/{filename1}";
            var writePath2 = $"{localExport}/{filename2}";
            var side2 = new Client(targetOS, targetRunner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        // Builds the sshfs server + the mount spec its clients use, plus (for the direct rows) the server node's own
        // runner and the LOCAL path of the export on that node. Returns nulls when the node is down (row self-skips).
        private static (Server?, SshfsMountSpec?, ProcessRunner?, string?) MakeSshfsServer(OS serverOS)
        {
            switch (serverOS)
            {
                case OS.Linux:
                    return linux_x64_2 is null ? default
                        : (new SshfsServer(linux_x64_2), LinuxSshfsSpec, linux_x64_2, SshfsServer.ExportDir);
                case OS.Mac:
                    if (mac_1 is null) return default;
                    var (priv, pub) = LabSshfsKey();
                    return (new MacSshfsServer(mac_1, pub),
                            new SshfsMountSpec(MacSshfsServer.Host, 22, MacSshfsServer.SshUser, MacSshfsServer.ExportDir, null, priv),
                            mac_1, MacSshfsServer.ExportDir);
                case OS.Windows:
                    return win10_x64_2 is null ? default
                        : (new WindowsSshfsServer(win10_x64_2),
                           new SshfsMountSpec(WindowsSshfsServer.Host, 22, win10Username ?? "", WindowsSshfsServer.ExportDir, win10Password, null),
                           win10_x64_2, WindowsSshfsServer.LocalExportDir);
                default:
                    return default;
            }
        }

        // 9P (Plan 9 protocol) served by diod over TCP - Linux-only, same client1 - server - client2
        // topology as NFS/sshfs: both clients mount the .81 diod export at an identical mount point.
        //
        // 9P (diod) is cross-client INCOHERENT for the append-and-tail-read pattern: a client never sees
        // another client's writes to a file it has open/cached (proven), so Normal and IsolatedIo
        // both truncate. UploadDownload sidesteps that (it transfers whole files), and with ft's
        // out-of-order reorder buffer it reassembles 9P's out-of-order file delivery correctly. So 9P is
        // supported only via --upload-download; that is the one mode tested here.
        [DataTestMethod]
        [DataRow(Mode.UploadDownload, DisplayName = "9P Linux-Linux-Linux UploadDownload")]
        public void NineP(Mode mode)
        {
            var ninePServer = new NinePServer(linux_x64_2); // .81 — hosts diod + /srv/9p

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = NinePPathLookup(filename1);
            var readPath1 = NinePPathLookup(filename2);
            var side1 = new NinePClient(OS.Linux, linux_x64_1, $"-w {writePath1} -r {readPath1} --verbose"); // .80

            var readPath2 = NinePPathLookup(filename1);
            var writePath2 = NinePPathLookup(filename2);
            var side2 = new NinePClient(OS.Linux, linux_x64_3, $"-r {readPath2} -w {writePath2} --verbose"); // .82

            ConductTunnelTests(mode, side1, ninePServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string NinePPathLookup(string fileName)
        {
            return $"{NinePClient.MountPoint}/{fileName.TrimStart('/')}";
        }

        // The nested QEMU guest on .82 is reached over the host's SSH port-forward (.82:2222). Lazily
        // created so ONLY the virtio tests depend on the nested guest being up - other tests are
        // unaffected if it isn't. ft writes to a guest-local log path (the guest has no //.31/r mount).
        private static LinuxProcessRunner? _linuxGuest;
        private static LinuxProcessRunner LinuxGuest => _linuxGuest ??=
            new LinuxProcessRunner("192.168.0.82", "user", "live", LINUX_X64_EXE, "/tmp/ft-guest.log", 2222);

        private static ConnectionInfo NestedGuestConnInfo() =>
            new("192.168.0.82", 2222, "user", new PasswordAuthenticationMethod("user", "live")) { Timeout = TimeSpan.FromSeconds(8) };

        // Confirm the nested QEMU guest can actually accept a deploy before a virtio test uses it, and refresh it
        // if not. A previous run can leave a WEDGED ft in the guest: a ReceivePump thread stuck on the virtio
        // mount keeps /tmp/ft/ft's text segment mapped, so scp fails with "Text file busy" - and it survives
        // kill -9 (the thread is in uninterruptible sleep), so the runner's own pkill can't clear it. The only
        // cure is a guest reboot. Returns false (the caller then skips) if the guest is unreachable or does not
        // come back - an infrastructure problem should skip the virtio rows, not fail the suite.
        private static bool EnsureNestedGuestHealthy()
        {
            bool wedged;
            try
            {
                using var ssh = new SshClient(NestedGuestConnInfo());
                ssh.Connect();
                // Non-destructive writability probe: opening the deployed binary for write ETXTBSYs while a hung
                // thread still maps it; bs=1 count=0 conv=notrunc changes nothing. An absent binary can't be wedged.
                var probeCmd = ssh.CreateCommand("if [ -f /tmp/ft/ft ]; then dd if=/tmp/ft/ft of=/tmp/ft/ft bs=1 count=0 conv=notrunc 2>&1; fi");
                probeCmd.CommandTimeout = TimeSpan.FromSeconds(15);
                var probe = probeCmd.Execute();
                ssh.Disconnect();
                wedged = probe.ToLowerInvariant().Contains("text file busy");
            }
            catch (Renci.SshNet.Common.SshOperationTimeoutException)
            {
                wedged = true; // the probe itself hung -> the guest is wedged; reboot it below
            }
            catch
            {
                return false; // guest unreachable -> caller skips
            }
            if (!wedged) return true;

            Console.WriteLine("Nested QEMU guest (.82:2222): ft binary wedged (Text file busy from a hung thread). Rebooting to refresh.");
            try
            {
                using var ssh = new SshClient(NestedGuestConnInfo());
                ssh.Connect();
                // Force the reboot: the hung thread is in uninterruptible sleep, so a clean shutdown stalls on it
                // (systemd waits out its stop timeout). -f goes straight to the kernel reboot.
                try { var rc = ssh.CreateCommand("sudo reboot -f 2>/dev/null || sudo systemctl reboot -ff 2>/dev/null || sudo reboot"); rc.CommandTimeout = TimeSpan.FromSeconds(15); rc.Execute(); } catch { /* connection drops as it goes down */ }
                try { ssh.Disconnect(); } catch { }
            }
            catch { }
            _linuxGuest = null; // the cached runner's SSH session is dead now; force a fresh deploy once it's back

            var deadline = DateTime.UtcNow.AddSeconds(150);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5000);
                try
                {
                    using var ssh = new SshClient(NestedGuestConnInfo());
                    ssh.Connect();
                    var upCmd = ssh.CreateCommand("true"); upCmd.CommandTimeout = TimeSpan.FromSeconds(8); upCmd.Execute();
                    ssh.Disconnect();
                    Thread.Sleep(3000); // small settle after boot before we deploy + mount
                    return true;
                }
                catch { /* still rebooting */ }
            }
            Console.WriteLine("Nested QEMU guest did not return within 150s of the reboot.");
            return false;
        }

        // virtio-fs (host <-> nested guest): host side is the native /srv/ftvfs (ext4); guest side is the
        // virtio-fs mount. ft auto-detects virtio-fs (mountinfo fstype) and runs Normal - its held handle
        // refreshes via ForceRead's fstat, ~2.4x faster than IsolatedIo' reopen. (sshfs, the other FUSE
        // family member, still gets IsolatedIo.) Confirmed on a real QEMU virtio-fs mount.
        [TestMethod]
        public void VirtioFs()
        {
            if (!EnsureNestedGuestHealthy())
                Assert.Inconclusive("Skipped: nested QEMU guest (.82:2222) unavailable or could not be refreshed.");

            var server = new VirtioFsServer(linux_x64_3); // .82 host - virtiofsd + the nested guest

            var f1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var f2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            // both sides run Normal: host on native ext4 (coherent), guest on virtio-fs (auto-detected -> Normal)
            var side1 = new Client(OS.Linux, linux_x64_3, $"-w {VirtioFsServer.HostExportDir}/{f1} -r {VirtioFsServer.HostExportDir}/{f2} --verbose");
            var side2 = VirtioGuestClient.VirtioFs(LinuxGuest, $"-r {VirtioGuestClient.VirtioFsMountPoint}/{f1} -w {VirtioGuestClient.VirtioFsMountPoint}/{f2} --verbose");

            ConductTunnelTests(Mode.Normal, side1, server, side2,
                $"{VirtioFsServer.HostExportDir}/{f2}", $"{VirtioFsServer.HostExportDir}/{f1}",
                $"{VirtioGuestClient.VirtioFsMountPoint}/{f1}", $"{VirtioGuestClient.VirtioFsMountPoint}/{f2}");
        }

        // virtio-9p (host <-> nested guest): host side is the native /srv/ft9p (ext4); guest side is the
        // virtio-9p mount. QEMU's -virtfs reports the BACKING fs's statfs magic (not V9FS), so ft sees ext4
        // and runs Normal - which is correct, since QEMU virtio-9p (cache=none) is coherent (unlike diod's
        // TCP-9p, which is V9FS + incoherent -> upload-download). Confirmed working in Normal on a real mount.
        [TestMethod]
        public void Virtio9p()
        {
            if (!EnsureNestedGuestHealthy())
                Assert.Inconclusive("Skipped: nested QEMU guest (.82:2222) unavailable or could not be refreshed.");

            var server = new Virtio9pServer(linux_x64_3);

            var f1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var f2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var side1 = new Client(OS.Linux, linux_x64_3, $"-w {Virtio9pServer.HostExportDir}/{f1} -r {Virtio9pServer.HostExportDir}/{f2} --verbose");
            var side2 = VirtioGuestClient.Virtio9p(LinuxGuest, $"-r {VirtioGuestClient.Virtio9pMountPoint}/{f1} -w {VirtioGuestClient.Virtio9pMountPoint}/{f2} --verbose");

            ConductTunnelTests(Mode.Normal, side1, server, side2,
                $"{Virtio9pServer.HostExportDir}/{f2}", $"{Virtio9pServer.HostExportDir}/{f1}",
                $"{VirtioGuestClient.Virtio9pMountPoint}/{f1}", $"{VirtioGuestClient.Virtio9pMountPoint}/{f2}");
        }

        // The Rdp mstsc row. Normal is reliable (see RdpServer for the April-2026 consent-dialog handling), so
        // it runs in the clean suite. IsolatedIo is split into its own KnownFlaky method below - both still
        // run; the tag only lets you exclude IR with `--filter TestCategory!=KnownFlaky` for a clean green.
        [DataTestMethod]
        [DataRow(Mode.Normal)]
        public void Rdp(Mode mode) => ConductRdpTest(mode);

        private void ConductRdpTest(Mode mode)
        {
            if (win10_x64_1 is null || win10_x64_2 is null)
                Assert.Inconclusive("Skipped: the Windows client (.83) or server (.84) node is unavailable.");

            // side1 (.83, client) runs mstsc into side2 (.84, server) with side1's C: redirected, so side2 sees
            // the same bytes at \\tsclient\c. The server's distinct machine SID lets the same-SID client
            // authenticate the RDP session (clone->clone RDP is rejected on 24H2+). See RdpServer.
            var server = new RdpServer(win10_x64_1, win10_x64_2.RunOnIP, win10Username ?? "", win10Password ?? "");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = $@"C:\Temp\{filename1}";   // side1 (.83)'s own C:\Temp
            var readPath1 = $@"C:\Temp\{filename2}";
            var side1 = new Client(OS.Windows, win10_x64_1, $"-w {writePath1} -r {readPath1}");

            var readPath2 = $@"\\tsclient\c\Temp\{filename1}";
            var writePath2 = $@"\\tsclient\c\Temp\{filename2}";
            var side2 = new Client(OS.Windows, win10_x64_2, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        // ft over an RDP redirected drive, driven from a LINUX RDP client (xfreerdp3 under Xvfb) instead
        // of mstsc - see RdpLinuxServer for the session mechanics. Unlike Rdp above, no hand-made RDP
        // session is required: the test establishes it, which is the whole point of this row.
        //
        // Pointed at client2 (.85): the mstsc Rdp row above uses the server VM (.84) as its side2, so the two
        // RDP rows use different Windows boxes - each needs a different drive redirected into the one
        // interactive session that user is allowed. (Linux -> .85 has no SID collision - the client is Linux.)
        //
        // Normal mode only. IsolatedIo does work over FreeRDP redirection (unlike mstsc's, where it
        // fails ~100%), but at ~0.12 MB/s vs ~8 MB/s - measured 8 MB in 67s - so it would dominate the
        // suite's runtime and sit uncomfortably close to ConductTest's 180s budget.
        [DataTestMethod]
        [DataRow(Mode.Normal)]
        public void RdpLinux(Mode mode)
        {
            var server = new RdpLinuxServer(linux_x64_1, win10_x64_3.RunOnIP, win10Username ?? "", win10Password ?? "");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            // side1 (.80) sees the directory natively; side2 (.85) sees the same bytes through RDPDR.
            var writePath1 = $"{RdpLinuxServer.ExportDir}/{filename1}";
            var readPath1 = $"{RdpLinuxServer.ExportDir}/{filename2}";
            var side1 = new Client(OS.Linux, linux_x64_1, $"-w {writePath1} -r {readPath1}");

            var readPath2 = RdpLinuxServer.RedirectedPath(filename1);
            var writePath2 = RdpLinuxServer.RedirectedPath(filename2);
            var side2 = new Client(OS.Windows, win10_x64_3, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedIo)]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedIo)]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedIo)]
        public void VirtualBoxSharedFolder(OS client1OS, OS client2OS, Mode mode)
        {
            // The shared storage is the dev box's C:\ (the c_drive VBox shared folder). Both tunnel ends are
            // now VBox GUESTS (client1 moved off the dev box onto a dedicated VM), so a Windows client reads
            // it via \\vboxsvr\c_drive and a Linux client via /media/vboxsf - guest<->guest. The host<->guest
            // path is covered separately by VirtualBoxSharedFolderHostToGuest.
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive("Skipped: a required VBox-guest node is unavailable.");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = VboxGuestPath(client1OS, filename1);
            var readPath1 = VboxGuestPath(client1OS, filename2);
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = VboxGuestPath(client2OS, filename1);
            var writePath2 = VboxGuestPath(client2OS, filename2);
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, new Server(OS.Windows, FileShareType.VirtualBoxSharedFolder), side2, readPath1, writePath1, readPath2, writePath2);
        }

        /// <summary>The path a VBox GUEST reaches the dev box's shared C:\Temp by.</summary>
        private static string VboxGuestPath(OS os, string filename) => os switch
        {
            OS.Windows => $@"\\vboxsvr\c_drive\Temp\{filename}",
            _ => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename}"
        };

        // Host->guest: side1 is the dev box running ft LOCALLY (its own C:\Temp IS the c_drive share), side2 is
        // a VBox guest reading the same bytes via \\vboxsvr / /media/vboxsf. Confirms the host-side path still
        // works now that the general Windows client1 is a guest VM (per the user's "dev box to VM" request).
        [DataTestMethod]
        [DataRow(OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, Mode.Normal)]
        public void VirtualBoxSharedFolderHostToGuest(OS client2OS, Mode mode)
        {
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            if (client2Runner is null)
                Assert.Inconclusive("Skipped: a required VBox-guest node is unavailable.");

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = $@"C:\Temp\{filename1}";   // the dev box's own C:\Temp
            var readPath1 = $@"C:\Temp\{filename2}";
            var side1 = new Client(OS.Windows, devBoxLocal, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = VboxGuestPath(client2OS, filename1);
            var writePath2 = VboxGuestPath(client2OS, filename2);
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, new Server(OS.Windows, FileShareType.VirtualBoxSharedFolder), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // The full client permutation matrix for the network-backend rows (FTP/WebDav/S3/Dropbox): every
        // (client1, client2) over {Windows, Linux, Mac, Android}. A row whose node is down self-skips (guard below).
        public static IEnumerable<object[]> AllClientCombos =>
            from c1 in new[] { OS.Windows, OS.Linux, OS.Mac, OS.Android }
            from c2 in new[] { OS.Windows, OS.Linux, OS.Mac, OS.Android }
            select new object[] { c1, c2 };

        // DynamicData row name in the SMB rows' "<Backend> <client1>-<server>-<client2>[ <mode>]" convention, naming
        // the single fixed server the backend uses (which its name alone doesn't reveal): WebDav/S3/FTP all go
        // through the .81 Linux node, Dropbox through the cloud. (Sshfs has its own varying-server axis -> SshfsRow.)
        public static string ServerNamedRow(System.Reflection.MethodInfo methodInfo, object[] data)
        {
            var server = methodInfo.Name == "Dropbox" ? "cloud" : "Linux";
            var mode = data.Length > 2 ? $" {data[2]}" : "";
            return $"{methodInfo.Name} {data[0]}-{server}-{data[1]}{mode}";
        }

        // side1 runs on client1's node, side2 on client2's. Android uses the one emulator's two ft instances
        // (android_1/android_2) and the Mac its two (mac_1/mac_2), so both sides can share one physical device.
        static ProcessRunner Client1Runner(OS os) => os switch
        { OS.Windows => win10_x64_1, OS.Mac => mac_1, OS.Android => android_1, _ => linux_x64_1 };
        static ProcessRunner Client2Runner(OS os) => os switch
        { OS.Windows => win10_x64_3, OS.Mac => mac_2, OS.Android => android_2, _ => linux_x64_3 };

        [DataTestMethod]
        [DynamicData(nameof(AllClientCombos), DynamicDataDisplayName = nameof(ServerNamedRow))]
        public void FTP(OS client1OS, OS client2OS)
        {
            var writePath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = Client1Runner(client1OS);
            var client2Runner = Client2Runner(client2OS);
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive($"Skipped: a required node is unavailable (FTP {client1OS}-{client2OS}).");
            var side1 = new Client(client1OS, client1Runner, $"--ftp -u anonymous -h 192.168.0.81 -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var side2 = new Client(client2OS, client2Runner, $"--ftp -u anonymous -h 192.168.0.81 -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.FTP, side1, new Server(OS.Linux, FileShareType.FTP), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // WebDAV (nginx on .81:8080) - an HTTP-API backend like FTP: ft talks to the server directly,
        // so the clients need no mounts. Rides UploadDownload with the blocking ping-pong reader;
        // Program.cs applies a 50ms pace floor so idle absent-slot polling doesn't hammer
        // billable/rate-limited endpoints (~270 req/s unpaced on a LAN; ~7 req/s with the floor).
        [DataTestMethod]
        [DynamicData(nameof(AllClientCombos), DynamicDataDisplayName = nameof(ServerNamedRow))]
        public void WebDav(OS client1OS, OS client2OS)
        {
            const string url = "http://192.168.0.81:8080/dav/";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = Client1Runner(client1OS);
            var client2Runner = Client2Runner(client2OS);
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive($"Skipped: a required node is unavailable (WebDav {client1OS}-{client2OS}).");
            var side1 = new Client(client1OS, client1Runner, $"--webdav --url {url} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var side2 = new Client(client2OS, client2Runner, $"--webdav --url {url} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.HttpApi, side1, new Server(OS.Linux, FileShareType.WebDav), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // S3 (MinIO on .81:9000, bucket 'fttest') - exercises ft's native SigV4 signer end-to-end
        // against a strictly-validating server. MinIO specifically: it is strongly consistent like real
        // AWS S3; `rclone serve s3` is NOT (its VFS caches object presence for minutes, deadlocking
        // ft's single-slot rapid write/delete handoff mid-transfer). Bucket names must be >= 3 chars,
        // hence 'fttest'. Throwaway lab-only keys, same convention as the other lab credentials.
        // Android S3 rows exercise SigV4 crypto: works because the emulator provisioning bundles a real Bionic
        // OpenSSL that AndroidProcessRunner puts on LD_LIBRARY_PATH (Android's own BoringSSL lacks the symbols .NET
        // binds - "a2d_ASN1_OBJECT"; real Termux: `pkg install openssl`).
        [DataTestMethod]
        [DynamicData(nameof(AllClientCombos), DynamicDataDisplayName = nameof(ServerNamedRow))]
        public void S3(OS client1OS, OS client2OS)
        {
            const string s3Args = "--s3 --bucket fttest --endpoint http://192.168.0.81:9000 --access-key ftaccess --secret-key ftsecret";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = Client1Runner(client1OS);
            var client2Runner = Client2Runner(client2OS);
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive($"Skipped: a required node is unavailable (S3 {client1OS}-{client2OS}).");
            var side1 = new Client(client1OS, client1Runner, $"{s3Args} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var side2 = new Client(client2OS, client2Runner, $"{s3Args} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.HttpApi, side1, new Server(OS.Linux, FileShareType.S3), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // Dropbox (native --dropbox client) against a REAL Dropbox account, across the full client matrix
        // (every client1/client2 over Windows/Linux/Mac/Android - the Android rows exercise the bundled Bionic
        // OpenSSL like S3, since Dropbox is HTTPS). There is no local Dropbox emulator, so this test is opt-in:
        // it self-skips (Assert.Inconclusive) unless dropbox_app_key / dropbox_app_secret / dropbox_refresh_token
        // are set in user-secrets, so it never breaks a normal run. Both ends share one Dropbox app folder; each
        // row uses random path names so concurrent/sequential rows never collide. A small payload is used because
        // Dropbox's per-request latency makes the default 5 MB transfer far too slow for the 180s per-test budget
        // (a 2 MB round-trip measured ~25s). ft auto-applies its Dropbox tuning. NOTE: the credentials appear on
        // the ft command line here (fine for a throwaway, app-folder-scoped, revocable test token).
        [DataTestMethod]
        [DynamicData(nameof(AllClientCombos), DynamicDataDisplayName = nameof(ServerNamedRow))]
        public void Dropbox(OS client1OS, OS client2OS)
        {
            if (string.IsNullOrEmpty(dropboxAppKey) || string.IsNullOrEmpty(dropboxAppSecret) || string.IsNullOrEmpty(dropboxRefreshToken))
            {
                Assert.Inconclusive("Dropbox credentials not configured (set dropbox_app_key / dropbox_app_secret / dropbox_refresh_token in user-secrets). Skipping the Dropbox end-to-end test.");
                return;
            }

            var dbArgs = $"--dropbox --app-key {dropboxAppKey} --app-secret {dropboxAppSecret} --refresh-token {dropboxRefreshToken}";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = Client1Runner(client1OS);
            var client2Runner = Client2Runner(client2OS);
            if (client1Runner is null || client2Runner is null)
                Assert.Inconclusive($"Skipped: a required node is unavailable (Dropbox {client1OS}-{client2OS}).");
            var side1 = new Client(client1OS, client1Runner, $"{dbArgs} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var side2 = new Client(client2OS, client2Runner, $"{dbArgs} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.HttpApi, side1, new Server(OS.Linux, FileShareType.Dropbox), side2, readPath1, writePath1, readPath2, writePath2, bytesToSend: 128 * 1024);
        }


        // Cross-OS SOCKS dynamic-forwarding over SMB (the most reliable backend). side1 hosts the SOCKS
        // proxy (-D 0.0.0.0:5005); side2 is the exit. A REAL curl on side1's node drives the TCP leg (to the
        // internet AND to a controlled dev-box responder); the harness drives the UDP leg, since no common
        // CLI implements SOCKS5 UDP. (Windows,Linux) = Windows proxy -> Linux exit; (Linux,Windows) = the
        // reverse; plus same-OS baselines.
        //
        // Lab assumptions (check these first if it fails): curl is on PATH on every node (built into Win10+
        // and the Debian nodes); the nodes have internet + working DNS (example.com / 8.8.8.8); side2 can
        // reach the dev box (192.168.0.31) inbound on 5007/5008; and the R:\Temp release binaries include
        // the SOCKS commits.
        [DataTestMethod]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Windows)]
        [DataRow(OS.Windows, OS.Windows)]
        [DataRow(OS.Linux, OS.Linux)]
        public void Socks(OS client1OS, OS client2OS)
        {
            var server = new SmbServer(OS.Linux, linux_x64_2);   // SMB transport, server on .81

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SmbPathLookup(client1OS, OS.Linux, filename1);
            var readPath1 = SmbPathLookup(client1OS, OS.Linux, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1}");

            var readPath2 = SmbPathLookup(client2OS, OS.Linux, filename1);
            var writePath2 = SmbPathLookup(client2OS, OS.Linux, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2}");

            server.Restart();
            side1.Restart();
            side2.Restart();

            // A Windows client reaches the .81 Samba server through a cmdkey that must live in ft's interactive
            // session (session 1) - seed it before the cell, exactly as ConductTunnelTests does (idempotent; no-op
            // for a Linux client). Without it the Windows side can't open the tunnel files on \\192.168.0.81\data,
            // so its ft never comes online and the SOCKS proxy has no exit (counterpart stays Offline the whole run).
            EnsureWinClientSessionCred(client1OS, server.OS, client1Runner);
            EnsureWinClientSessionCred(client2OS, server.OS, client2Runner);

            // best-effort cleanup of stale tunnel files
            foreach (var (runner, path) in new[] { (side1.Runner, readPath1), (side1.Runner, writePath1), (side2.Runner, readPath2), (side2.Runner, writePath2) })
            {
                try { runner.DeleteFile(path); } catch { }
            }

            ConductTest(
                $"SOCKS {side1.OS}-{server.OS}-{side2.OS}",
                new Client(side1.OS, side1.Runner, $"{side1.Args} -D 0.0.0.0:{SOCKS_PROXY_PORT}"),
                server,
                new Client(side2.OS, side2.Runner, side2.Args),
                "SOCKS",
                transferOverride: ct => RunSocksChecks(side1, side2, ct));
        }

        static void RunSocksChecks(Client side1, Client side2, CancellationToken ct)
        {
            // side1 hosts -D 0.0.0.0:5005. curl runs ON side1's node against localhost:5005; the harness UDP
            // client (this process) reaches the same proxy at side1's IP:5005 (hence the 0.0.0.0 bind).
            var side1IP = side1.Runner.RunOnIP;
            var udpProxy = new IPEndPoint(IPAddress.Parse(side1IP), SOCKS_PROXY_PORT);

            // Invoke the REAL curl on Windows: the SSH shell on the Windows nodes is PowerShell, where a bare
            // `curl` is an alias for Invoke-WebRequest - which knows nothing of --socks5 and fails every check.
            // curl.exe is the genuine binary (built into Win10+); on Linux it stays plain `curl`.
            var curlBin = side1.OS == OS.Windows ? "curl.exe" : "curl";

            // 1) TCP via real curl -> the internet (also exercises far-side DNS resolution on the exit).
            //    Longer deadline: this is where we wait for the tunnel + SOCKS listener to come online.
            Retry("curl -> internet (example.com)", 90, ct, () =>
            {
                var (code, output) = side1.Runner.RunCommand($"{curlBin} -s --max-time 30 --socks5-hostname 127.0.0.1:{SOCKS_PROXY_PORT} http://example.com/");
                return code == 0 && output.Contains("Example Domain");
            });

            // 2) TCP via real curl -> a controlled dev-box responder (deterministic; a unique marker proves
            //    the exact content traversed the cross-OS tunnel).
            var marker = $"SOCKS-E2E-{Guid.NewGuid():N}";
            using (StartHttpResponder(SOCKS_HTTP_PORT, marker, ct))
            {
                Retry("curl -> controlled dev-box responder", 25, ct, () =>
                {
                    var (code, output) = side1.Runner.RunCommand($"{curlBin} -s --max-time 20 --socks5 127.0.0.1:{SOCKS_PROXY_PORT} http://{DEV_BOX_IP}:{SOCKS_HTTP_PORT}/");
                    return code == 0 && output.Contains(marker);
                });
            }

            // 3) UDP via the harness -> the internet (DNS query to 8.8.8.8).
            Retry("socks-udp -> internet DNS (8.8.8.8)", 25, ct, () =>
            {
                SocksTestClient.AssertUdpDnsResolves(udpProxy, "8.8.8.8", "example.com");
                return true;
            });

            // 4) UDP via the harness -> a controlled dev-box echo (byte integrity).
            using (StartUdpEcho(SOCKS_UDP_PORT, ct))
            {
                var payload = new byte[512];
                Random.Shared.NextBytes(payload);
                Retry("socks-udp -> controlled dev-box echo", 25, ct, () =>
                {
                    SocksTestClient.AssertUdpEcho(udpProxy, DEV_BOX_IP, SOCKS_UDP_PORT, payload);
                    return true;
                });
            }
        }

        static void Retry(string what, int deadlineSeconds, CancellationToken ct, Func<bool> check)
        {
            var start = DateTime.Now;
            Exception? last = null;
            while ((DateTime.Now - start).TotalSeconds < deadlineSeconds && !ct.IsCancellationRequested)
            {
                try { if (check()) return; }
                catch (Exception ex) { last = ex; }
                Thread.Sleep(2000);
            }
            throw new Exception($"SOCKS check failed: {what}{(last != null ? $" ({last.Message})" : "")}", last);
        }

        // A raw TCP listener on the dev box that answers any HTTP request with a fixed body carrying `marker`.
        // The SOCKS exit (side2) dials this; curl (through the proxy) then sees the marker. Deliberately raw
        // (not HttpListener) to avoid Windows URL-ACL/admin requirements.
        static IDisposable StartHttpResponder(int port, string marker, CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            var body = $"marker={marker}\n";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");

            new Thread(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        using var client = listener.AcceptTcpClient();
                        var stream = client.GetStream();
                        try { stream.ReadTimeout = 5000; stream.Read(new byte[4096], 0, 4096); } catch { }   // consume (ignore) the request
                        stream.Write(response, 0, response.Length);
                        stream.Flush();
                    }
                }
                catch { }
            })
            { IsBackground = true }.Start();

            return new Stopper(() => { try { listener.Stop(); } catch { } });
        }

        // A UDP echo server on the dev box (the controlled UDP destination the SOCKS exit dials).
        static IDisposable StartUdpEcho(int port, CancellationToken ct)
        {
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));

            new Thread(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var data = socket.Receive(ref from);
                        socket.Send(data, data.Length, from);
                    }
                }
                catch { }
            })
            { IsBackground = true }.Start();

            return new Stopper(() => { try { socket.Close(); } catch { } });
        }

        sealed class Stopper(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }

        // Cross-machine SOCKS STRESS: the same four-proxy topology as the hermetic SocksStress unit test, but
        // the two ft instances run on DIFFERENT lab machines over the real SMB tunnel. Both sides run -D + -R
        // (four proxies, two hosted per side).
        //
        // Each proxy is driven by curl running ON ITS HOST NODE, connecting over loopback (127.0.0.1) - the
        // way a SOCKS proxy is actually used. So nothing connects INBOUND across the network to a proxy port
        // (only the exit dials OUT to the dev-box server), which sidesteps the Windows-node inbound firewall
        // entirely and lets a Windows node host proxies too. curl downloads a large payload through every
        // local proxy at once; --fail makes a short/failed transfer a non-zero exit, so exit 0 == it all
        // arrived. (Windows,Linux) and (Linux,Windows) put the proxy hosts on different OSes both ways.
        [DataTestMethod]
        [Timeout(700000)]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Windows)]
        public void SocksStress(OS client1OS, OS client2OS)
        {
            var server = new SmbServer(OS.Linux, linux_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SmbPathLookup(client1OS, OS.Linux, filename1);
            var readPath1 = SmbPathLookup(client1OS, OS.Linux, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1}");

            var readPath2 = SmbPathLookup(client2OS, OS.Linux, filename1);
            var writePath2 = SmbPathLookup(client2OS, OS.Linux, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2}");

            server.Restart();
            side1.Restart();
            side2.Restart();

            // Seed the Windows client's session-1 cmdkey for the .81 Samba server (see Socks()); without it the
            // Windows side can't reach \\192.168.0.81\data, so its ft never comes online and the tunnel - and every
            // proxy riding it - stays dead. Idempotent; no-op for a Linux client.
            EnsureWinClientSessionCred(client1OS, server.OS, client1Runner);
            EnsureWinClientSessionCred(client2OS, server.OS, client2Runner);

            foreach (var (runner, path) in new[] { (side1.Runner, readPath1), (side1.Runner, writePath1), (side2.Runner, readPath2), (side2.Runner, writePath2) })
            {
                try { runner.DeleteFile(path); } catch { }
            }

            ConductTest(
                $"SOCKS-STRESS {side1.OS}-{server.OS}-{side2.OS}",
                new Client(side1.OS, side1.Runner, $"{side1.Args} -D 0.0.0.0:{STRESS_A_LOCAL} -R 0.0.0.0:{STRESS_A_REMOTE}"),
                server,
                new Client(side2.OS, side2.Runner, $"{side2.Args} -D 0.0.0.0:{STRESS_B_LOCAL} -R 0.0.0.0:{STRESS_B_REMOTE}"),
                "SOCKS-STRESS",
                transferOverride: ct => RunSocksStress(side1, side2, ct),
                timeoutSeconds: 600);
        }

        static void RunSocksStress(Client side1, Client side2, CancellationToken ct)
        {
            // Each proxy is exercised by curl running ON ITS HOST NODE against 127.0.0.1 (loopback) - so no
            // inbound cross-network connection to a proxy port is ever made. The only cross-network traffic is
            // the exit dialing OUT to the dev-box HTTP server, which every node can do.
            var proxies = new (ProcessRunner Runner, OS OS, int Port)[]
            {
                (side1.Runner, side1.OS, STRESS_A_LOCAL),    // side1's -D  (hosted on side1)
                (side1.Runner, side1.OS, STRESS_B_REMOTE),   // side2's -R  (hosted on side1)
                (side2.Runner, side2.OS, STRESS_A_REMOTE),   // side1's -R  (hosted on side2)
                (side2.Runner, side2.OS, STRESS_B_LOCAL),    // side2's -D  (hosted on side2)
            };

            using var httpServer = StartLargePayloadHttpServer(STRESS_HTTP_PORT, STRESS_PAYLOAD_BYTES, ct);

            // Fire all four downloads at once, so a large transfer is in flight through every proxy over the
            // one tunnel simultaneously. Each retries until the proxy/tunnel is online (curl fails fast on a
            // refused local port), then a single full download proves the payload got through end to end.
            var checks = proxies.Select(proxy => Task.Run(() =>
            {
                var nullDevice = proxy.OS == OS.Windows ? "NUL" : "/dev/null";
                var curlBin = proxy.OS == OS.Windows ? "curl.exe" : "curl";   // PowerShell aliases bare `curl` to Invoke-WebRequest
                var curl = $"{curlBin} -s --fail --max-time 200 -o {nullDevice} --socks5 127.0.0.1:{proxy.Port} http://{DEV_BOX_IP}:{STRESS_HTTP_PORT}/";

                var start = DateTime.Now;
                (int Code, string Output) last = (-1, "");
                while ((DateTime.Now - start).TotalSeconds < 240 && !ct.IsCancellationRequested)
                {
                    last = proxy.Runner.RunCommand(curl);
                    if (last.Code == 0) return;
                    Thread.Sleep(3000);
                }
                throw new Exception($"SOCKS download via local proxy 127.0.0.1:{proxy.Port} on {proxy.OS} did not succeed: exit={last.Code} {Truncate(last.Output)}");
            })).ToArray();

            try { Task.WaitAll(checks); }
            catch (AggregateException agg) { throw agg.Flatten().InnerExceptions.FirstOrDefault() ?? agg; }
        }

        // Serves one large fixed payload (with Content-Length) to every connection, each on its own thread so
        // the four concurrent downloads aren't serialized. The SOCKS exits dial this. curl --fail turns any
        // short read into a non-zero exit, so a completed download proves the whole payload traversed the tunnel.
        static IDisposable StartLargePayloadHttpServer(int port, int payloadBytes, CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            var payload = new byte[payloadBytes];
            Random.Shared.NextBytes(payload);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {payloadBytes}\r\nConnection: close\r\n\r\n");

            new Thread(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var client = listener.AcceptTcpClient();
                        new Thread(() =>
                        {
                            try
                            {
                                using (client)
                                {
                                    var stream = client.GetStream();
                                    try { stream.ReadTimeout = 5000; stream.Read(new byte[4096], 0, 4096); } catch { }   // consume the request
                                    stream.Write(header, 0, header.Length);
                                    stream.Write(payload, 0, payload.Length);
                                    stream.Flush();
                                }
                            }
                            catch { }
                        })
                        { IsBackground = true }.Start();
                    }
                }
                catch { }
            })
            { IsBackground = true }.Start();

            return new Stopper(() => { try { listener.Stop(); } catch { } });
        }

        static string Truncate(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s[..300]);

        public static void ConductTunnelTests(Mode mode, Client side1, Server server, Client side2, string readPath1, string writePath1, string readPath2, string writePath2, int bytesToSend = 5 * 1024 * 1024)
        {
            var cleanupFiles = new Action(() =>
            {
                Task[] deleteTasks = [
                    Task.Factory.StartNew(() => side1.Runner.DeleteFile(readPath1)),
                    Task.Factory.StartNew(() => side1.Runner.DeleteFile(writePath1)),
                    Task.Factory.StartNew(() => side2.Runner.DeleteFile(readPath2)),
                    Task.Factory.StartNew(() => side2.Runner.DeleteFile(writePath2))];

                try
                {
                    Task.WaitAll(deleteTasks, 10000);
                }
                catch { }
            });

            var name = $"{server.FileShareType} {side1.OS}-{server.OS}-{side2.OS}";


            if (mode == Mode.Normal)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (Normal mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:127.0.0.1:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "Normal", bytesToSend);
            }



            if (mode == Mode.IsolatedIo)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (IsolatedIo mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:127.0.0.1:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004 --isolated-io"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args} --isolated-io"),
                        "IsolatedIo", bytesToSend);
            }



            if (mode == Mode.UploadDownload)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                //9P is fully auto-configured from the mount type in Program.cs: statfs detects the 9P
                //mount, auto-enables --upload-download, and applies the 64KB cap + 10ms pace. So we pass
                //NEITHER --upload-download NOR --pace here - this exercises that detection end-to-end.
                ConductTest(
                        $"{name} (Upload-Download mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:127.0.0.1:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, side2.Args),
                        "Upload-Download", bytesToSend);
            }

            if (mode == Mode.FTP)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (FTP mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:127.0.0.1:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "FTP", bytesToSend);
            }

            //WebDAV / S3-native: the backend flag is already in the client Args (like --ftp), and the
            //transport tuning (pace floor) is applied by Program.cs, so no extra args here.
            if (mode == Mode.HttpApi)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (HTTP API mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:127.0.0.1:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "HTTP API", bytesToSend);
            }
        }

        public static void ConductTest(string name, Client side1, Server server, Client side2, string mode, int bytesToSend = 5 * 1024 * 1024, Action<CancellationToken>? transferOverride = null, int timeoutSeconds = 180)
        {

            var testNumberStr = $"Test {testNumber++}";
            TestOutputLog.AppendLine(localWindowsOutputFilename, testNumberStr);

            csvWriter.NextRecord();

            var sw = Stopwatch.StartNew();

            side1.Runner.Run(side1.Args);
            side2.Runner.Run(side2.Args);

            var results = new BlockingCollection<(bool Success, string Errror)>();

            var stop = new CancellationTokenSource();

            var transfersTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    if (transferOverride != null)
                        transferOverride(stop.Token);
                    else
                        TestTransfer(bytesToSend, true, 2, side1.Runner.RunOnIP, stop.Token);
                    results.Add((true, ""));
                }
                catch (Exception ex)
                {
                    results.Add((false, ex.Message));
                }
            }, TaskCreationOptions.LongRunning);


            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            (bool Success, string Errror) result;
            try
            {
                result = results.Take(timeout.Token);
            }
            catch
            {
                result = (false, "Did not finish");
            }

            stop.Cancel();
            // Bound the wait: TestDirection's socket reads take no cancellation token, so a transfer that never
            // formed (e.g. a client's ft could not bind its ports) won't observe stop.Cancel(). Leak it rather
            // than block the whole suite forever - the result is already recorded as "Did not finish".
            if (!transfersTask.Wait(TimeSpan.FromSeconds(30)))
                Debug.WriteLine($"WARNING: transfer task for [{name}] did not stop within 30s; leaking it so the suite proceeds.");

            sw.Stop();

            csvWriter.WriteField(testNumberStr);

            if (result.Success)
            {
                Debug.WriteLine($@"""{name}"",""Pass"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"pass");
            }
            else
            {
                Debug.WriteLine($@"""{name}"",""Fail"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"fail");
            }

            csvWriter.WriteField($"{sw.Elapsed.TotalSeconds:N3}");

            csvWriter.WriteField($"{server.FileShareType}");
            csvWriter.WriteField($"{mode}");
            csvWriter.WriteField($"{side1.OS}");
            csvWriter.WriteField($"{server.OS}");
            csvWriter.WriteField($"{side2.OS}");



            var side1Duration = side1.Runner.Stop();
            var side2Duration = side2.Runner.Stop();

            csvWriter.WriteField(side1Duration?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "");
            csvWriter.WriteField(side2Duration?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "");

            totalCpuUsageMs += side1Duration?.TotalMilliseconds ?? 0;
            totalCpuUsageMs += side2Duration?.TotalMilliseconds ?? 0;


            var command1 = side1.Runner.GetFullCommand(side1.Args);
            csvWriter.WriteField(command1);

            var command2 = side2.Runner.GetFullCommand(side2.Args);
            csvWriter.WriteField(command2);


            if (result.Success)
            {
                csvWriter.WriteField($"");
            }
            else
            {
                csvWriter.WriteField(result.Errror);
            }

            csvWriter.Flush();



            TestOutputLog.AppendLine(localWindowsOutputFilename, "--------------------------------------------------------------------------------");

            Assert.IsTrue(result.Success, result.Errror);
        }

        public static (TcpClient connected, TcpClient accepted) EstablishConnection(TcpListener listener, IPEndPoint connectTo, CancellationToken cancelationToken)
        {
            var acceptConnectionTask = listener.AcceptTcpClientAsync(cancelationToken);

            var originClient = new TcpClient();

            var startTime = DateTime.Now;
            while (!cancelationToken.IsCancellationRequested)
            {
                var duration = DateTime.Now - startTime;
                if (duration.TotalSeconds > 150)
                {
                    throw new Exception("Could not connect");
                }

                try
                {
                    originClient.Connect(connectTo);
                }
                catch
                {
                    Thread.Sleep(200);
                    continue;
                }

                break;
            }


            while (!acceptConnectionTask.IsCompleted && acceptConnectionTask.IsCompletedSuccessfully && !cancelationToken.IsCancellationRequested)
            {
                Thread.Sleep(200);
            }
            var acceptedConnection = acceptConnectionTask.Result;

            return (originClient, acceptedConnection);
        }


        public static void TestTransfer(int bytesToSend, bool fullDuplex, int connections, string connectToIP, CancellationToken cancelationToken)
        {
            var ultimateDestination = new TcpListener($"0.0.0.0:5004".AsEndpoint());
            ultimateDestination.Start();

            try
            {
                var establishedConnections = Enumerable
                                                .Range(0, connections)
                                                .Select(connection =>
                                                {
                                                    var connectTo = $"{connectToIP}:5002".AsEndpoint();
                                                    (var originClient, var ultimateDestinationClient) = EstablishConnection(ultimateDestination, connectTo, cancelationToken);

                                                    Debug.WriteLine($"Accepted connection from: {ultimateDestinationClient.Client.RemoteEndPoint}");

                                                    return new
                                                    {
                                                        OriginClient = originClient,
                                                        UltimateDestinationClient = ultimateDestinationClient
                                                    };
                                                })
                                                .ToList();

                if (cancelationToken.IsCancellationRequested)
                {
                    throw new Exception($"Connections were not established within the timeout window");
                }

                establishedConnections
                    .AsParallel()
                    .WithDegreeOfParallelism(connections)
                    .ForAll(pair =>
                    {
                        var toSend = new byte[bytesToSend];
                        Random.Shared.NextBytes(toSend);

                        var tests = new[]
                        {
                            new Action(() => TransferVerification.TestDirection("Forward", pair.OriginClient, pair.UltimateDestinationClient, toSend)),
                            new Action(() => TransferVerification.TestDirection("Reverse", pair.UltimateDestinationClient, pair.OriginClient, toSend)),
                        };

                        if (fullDuplex)
                        {
                            var testTasks = tests
                                                .ToList()
                                                .Select(test => Task.Factory.StartNew(test, TaskCreationOptions.LongRunning))
                                                .ToArray();

                            Task.WaitAll(testTasks, cancelationToken);
                        }
                        else
                        {
                            foreach (var test in tests)
                            {
                                test();
                            }
                        }
                    });

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                throw;
            }
            finally
            {
                ultimateDestination.Stop();
            }
        }

        // Transfer-and-verify moved to the shared ft_tests.Utilities.TransferVerification.TestDirection
        // (used by both this suite and TcpUnitTests) so the integrity assertion can't silently drift.
    }

    public enum OS
    {
        Windows,
        Linux,
        Mac,
        Android
    }

    public enum FileShareType
    {
        SMB,
        NFS,
        Sshfs,
        NineP,
        FTP,
        WebDav,
        S3,
        Dropbox,

        RDP,
        RdpLinux,

        VirtualBoxSharedFolder,

        VirtioFs,
        Virtio9p
    }

    public enum Mode
    {
        Normal,
        IsolatedIo,
        UploadDownload,
        FTP,
        HttpApi
    }
}
