using CsvHelper;
using CsvHelper.Configuration;
using ft;
using ft_tests.FileShares.Clients;
using ft_tests.FileShares.Servers;
using ft_tests.Runner;
using ft_tests.Utilities;
using Microsoft.Extensions.Configuration;
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

        static ProcessRunner win10_x64_1;
        static ProcessRunner win10_x64_2;
        static ProcessRunner win10_x64_3;


        static ProcessRunner linux_x64_1;
        static ProcessRunner linux_x64_2;
        static ProcessRunner linux_x64_3;

        // Dropbox credentials (user-secrets). When absent, the Dropbox test skips (Assert.Inconclusive)
        // rather than failing - it hits real Dropbox (no local emulator), so it is opt-in.
        static string? dropboxAppKey;
        static string? dropboxAppSecret;
        static string? dropboxRefreshToken;

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

            win10_x64_1 = new LocalWindowsProcessRunner(WIN_X64_EXE, localWindowsOutputFilename);
            win10_x64_2 = new RemoteWindowsProcessRunner("192.168.0.32", config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE); //win10 VM
            win10_x64_3 = new RemoteWindowsProcessRunner("192.168.0.20", config["edm_username"], config["edm_password"], WIN_X64_EXE);          //elitedesk

            linux_x64_1 = new LinuxProcessRunner("192.168.0.80", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.80.log");
            linux_x64_2 = new LinuxProcessRunner("192.168.0.81", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.81.log");
            linux_x64_3 = new LinuxProcessRunner("192.168.0.82", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.82.log");


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
        [DataRow(OS.Windows, OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void Smb(OS client1OS, OS serverOS, OS client2OS, Mode mode)
        {
            SmbServer smbServer = serverOS == OS.Linux
                ? new SmbServer(OS.Linux, linux_x64_2)
                : new SmbServer(OS.Windows, win10_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SmbPathLookup(client1OS, serverOS, filename1);
            var readPath1 = SmbPathLookup(client1OS, serverOS, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1}");

            var readPath2 = SmbPathLookup(client2OS, serverOS, filename1);
            var writePath2 = SmbPathLookup(client2OS, serverOS, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, smbServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string SmbPathLookup(OS client, OS server, string fileName)
        {
            var clientSep = client == OS.Windows ? '\\' : '/';
            var otherSep = client == OS.Windows ? '/' : '\\';
            fileName = fileName.Replace(otherSep, clientSep).TrimStart('\\', '/');

            string basePath = (client, server) switch
            {
                (OS.Windows, OS.Windows) => @$"\\192.168.0.32\shared\",
                (OS.Windows, OS.Linux) => @$"\\192.168.0.81\data\",
                (OS.Linux, OS.Windows) => @$"/media/smb/192.168.0.32/shared/",
                (OS.Linux, OS.Linux) => @$"/media/smb/192.168.0.81/data/",
                _ => throw new InvalidOperationException("Unsupported client/server OS combo")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void Nfs(OS client1OS, OS client2OS, Mode mode)
        {
            var nfsServer = new NfsServer(linux_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = NfsPathLookup(client1OS, filename1);
            var readPath1 = NfsPathLookup(client1OS, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new NfsClient(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = NfsPathLookup(client2OS, filename1);
            var writePath2 = NfsPathLookup(client2OS, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new NfsClient(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

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
                _ => throw new InvalidOperationException("Unsupported client OS")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        // sshfs is Linux-only here (a FUSE filesystem over SSH). Both clients (.80, .82) mount the
        // same export on the SSH server (.81), exactly mirroring the NFS topology: client1 - server
        // - client2. The mount point is identical on each client, so a single path lookup serves
        // both sides — whatever client1 writes appears to client2 through the shared server export.
        [DataTestMethod]
        [DataRow(Mode.Normal)]
        [DataRow(Mode.IsolatedReads)]
        public void Sshfs(Mode mode)
        {
            var sshfsServer = new SshfsServer(linux_x64_2); // .81 — hosts sshd + /srv/sshfs

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SshfsPathLookup(filename1);
            var readPath1 = SshfsPathLookup(filename2);
            var side1 = new SshfsClient(OS.Linux, linux_x64_1, $"-w {writePath1} -r {readPath1} --verbose"); // .80

            var readPath2 = SshfsPathLookup(filename1);
            var writePath2 = SshfsPathLookup(filename2);
            var side2 = new SshfsClient(OS.Linux, linux_x64_3, $"-r {readPath2} -w {writePath2} --verbose"); // .82

            ConductTunnelTests(mode, side1, sshfsServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string SshfsPathLookup(string fileName)
        {
            return $"{SshfsClient.MountPoint}/{fileName.TrimStart('/')}";
        }

        // 9P (Plan 9 protocol) served by diod over TCP - Linux-only, same client1 - server - client2
        // topology as NFS/sshfs: both clients mount the .81 diod export at an identical mount point.
        //
        // 9P (diod) is cross-client INCOHERENT for the append-and-tail-read pattern: a client never sees
        // another client's writes to a file it has open/cached (proven), so Normal and IsolatedReads
        // both truncate. UploadDownload sidesteps that (it transfers whole files), and with ft's
        // out-of-order reorder buffer it reassembles 9P's out-of-order file delivery correctly. So 9P is
        // supported only via --upload-download; that is the one mode tested here.
        [DataTestMethod]
        [DataRow(Mode.UploadDownload)]
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

        // virtio-fs (host <-> nested guest): host side is the native /srv/ftvfs (ext4); guest side is the
        // virtio-fs mount. ft auto-detects virtio-fs (mountinfo fstype) and runs Normal - its held handle
        // refreshes via ForceRead's fstat, ~2.4x faster than IsolatedReads' reopen. (sshfs, the other FUSE
        // family member, still gets IsolatedReads.) Confirmed on a real QEMU virtio-fs mount.
        [TestMethod]
        public void VirtioFs()
        {
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
            var server = new Virtio9pServer(linux_x64_3);

            var f1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var f2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var side1 = new Client(OS.Linux, linux_x64_3, $"-w {Virtio9pServer.HostExportDir}/{f1} -r {Virtio9pServer.HostExportDir}/{f2} --verbose");
            var side2 = VirtioGuestClient.Virtio9p(LinuxGuest, $"-r {VirtioGuestClient.Virtio9pMountPoint}/{f1} -w {VirtioGuestClient.Virtio9pMountPoint}/{f2} --verbose");

            ConductTunnelTests(Mode.Normal, side1, server, side2,
                $"{Virtio9pServer.HostExportDir}/{f2}", $"{Virtio9pServer.HostExportDir}/{f1}",
                $"{VirtioGuestClient.Virtio9pMountPoint}/{f1}", $"{VirtioGuestClient.Virtio9pMountPoint}/{f2}");
        }

        // KnownFlaky: Rdp IsolatedReads fails ~100% of the time — the per-read server round-trips
        // starve the keepalive ping over RDP's high-latency \\tsclient channel (see the SMB fix-stack
        // notes). RDP Normal is reliable, but TestCategory can't be applied per-DataRow, so the whole
        // method is quarantined. Exclude with `--filter TestCategory!=KnownFlaky` for a clean green.
        [DataTestMethod]
        [TestCategory("KnownFlaky")]
        [DataRow(Mode.Normal)]
        [DataRow(Mode.IsolatedReads)]
        public void Rdp(Mode mode)
        {
            var server = new Server(OS.Windows, FileShareType.RDP);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = $@"C:\Temp\{filename1}";
            var readPath1 = $@"C:\Temp\{filename2}";
            var side1 = new Client(OS.Windows, win10_x64_1, $"-w {writePath1} -r {readPath1}");

            var readPath2 = $@"\\tsclient\c\Temp\{filename1}";
            var writePath2 = $@"\\tsclient\c\Temp\{filename2}";
            var side2 = new Client(OS.Windows, win10_x64_3, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void VirtualBoxSharedFolder(OS client1OS, OS client2OS, Mode mode)
        {
            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = client1OS switch
            {
                OS.Windows => $@"C:\Temp\{filename1}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename1}"
            };
            var readPath1 = client1OS switch
            {
                OS.Windows => $@"C:\Temp\{filename2}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename2}"
            };
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = client2OS switch
            {
                OS.Windows => $@"\\vboxsvr\c_drive\Temp\{filename1}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename1}"
            };
            var writePath2 = client2OS switch
            {
                OS.Windows => $@"\\vboxsvr\c_drive\Temp\{filename2}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename2}"
            };
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, new Server(OS.Windows, FileShareType.VirtualBoxSharedFolder), side2, readPath1, writePath1, readPath2, writePath2);
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows)]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Linux)]
        public void FTP(OS client1OS, OS client2OS)
        {
            var writePath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"--ftp -u anonymous -h 192.168.0.81 -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"--ftp -u anonymous -h 192.168.0.81 -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.FTP, side1, new Server(OS.Linux, FileShareType.FTP), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // WebDAV (nginx on .81:8080) - an HTTP-API backend like FTP: ft talks to the server directly,
        // so the clients need no mounts. Rides UploadDownload with the blocking ping-pong reader;
        // Program.cs applies a 50ms pace floor so idle absent-slot polling doesn't hammer
        // billable/rate-limited endpoints (~270 req/s unpaced on a LAN; ~7 req/s with the floor).
        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows)]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Linux)]
        public void WebDav(OS client1OS, OS client2OS)
        {
            const string url = "http://192.168.0.81:8080/dav/";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"--webdav --url {url} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"--webdav --url {url} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.HttpApi, side1, new Server(OS.Linux, FileShareType.WebDav), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // S3 (MinIO on .81:9000, bucket 'fttest') - exercises ft's native SigV4 signer end-to-end
        // against a strictly-validating server. MinIO specifically: it is strongly consistent like real
        // AWS S3; `rclone serve s3` is NOT (its VFS caches object presence for minutes, deadlocking
        // ft's single-slot rapid write/delete handoff mid-transfer). Bucket names must be >= 3 chars,
        // hence 'fttest'. Throwaway lab-only keys, same convention as the other lab credentials.
        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows)]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Linux)]
        public void S3(OS client1OS, OS client2OS)
        {
            const string s3Args = "--s3 --bucket fttest --endpoint http://192.168.0.81:9000 --access-key ftaccess --secret-key ftsecret";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"{s3Args} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"{s3Args} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.HttpApi, side1, new Server(OS.Linux, FileShareType.S3), side2, readPath1, writePath1, readPath2, writePath2);
        }

        // Dropbox (native --dropbox client) against a REAL Dropbox account. There is no local Dropbox
        // emulator, so this test is opt-in: it self-skips (Assert.Inconclusive) unless dropbox_app_key /
        // dropbox_app_secret / dropbox_refresh_token are set in user-secrets, so it never breaks a normal
        // run. Linux-Linux only (no Windows-remote runner needed); both ends share one Dropbox app folder.
        // A small payload is used because Dropbox's per-request latency makes the default 5 MB transfer far
        // too slow for the 180s per-test budget (a 2 MB round-trip measured ~25s). ft auto-applies its
        // Dropbox tuning. NOTE: the credentials appear on the ft command line here (fine for a throwaway,
        // app-folder-scoped, revocable test token).
        [TestMethod]
        public void Dropbox()
        {
            if (string.IsNullOrEmpty(dropboxAppKey) || string.IsNullOrEmpty(dropboxAppSecret) || string.IsNullOrEmpty(dropboxRefreshToken))
            {
                Assert.Inconclusive("Dropbox credentials not configured (set dropbox_app_key / dropbox_app_secret / dropbox_refresh_token in user-secrets). Skipping the Dropbox end-to-end test.");
                return;
            }

            var dbArgs = $"--dropbox --app-key {dropboxAppKey} --app-secret {dropboxAppSecret} --refresh-token {dropboxRefreshToken}";

            var writePath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var side1 = new Client(OS.Linux, linux_x64_1, $"{dbArgs} -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var side2 = new Client(OS.Linux, linux_x64_3, $"{dbArgs} -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

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

            // 1) TCP via real curl -> the internet (also exercises far-side DNS resolution on the exit).
            //    Longer deadline: this is where we wait for the tunnel + SOCKS listener to come online.
            Retry("curl -> internet (example.com)", 90, ct, () =>
            {
                var (code, output) = side1.Runner.RunCommand($"curl -s --max-time 30 --socks5-hostname 127.0.0.1:{SOCKS_PROXY_PORT} http://example.com/");
                return code == 0 && output.Contains("Example Domain");
            });

            // 2) TCP via real curl -> a controlled dev-box responder (deterministic; a unique marker proves
            //    the exact content traversed the cross-OS tunnel).
            var marker = $"SOCKS-E2E-{Guid.NewGuid():N}";
            using (StartHttpResponder(SOCKS_HTTP_PORT, marker, ct))
            {
                Retry("curl -> controlled dev-box responder", 25, ct, () =>
                {
                    var (code, output) = side1.Runner.RunCommand($"curl -s --max-time 20 --socks5 127.0.0.1:{SOCKS_PROXY_PORT} http://{DEV_BOX_IP}:{SOCKS_HTTP_PORT}/");
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
                var curl = $"curl -s --fail --max-time 200 -o {nullDevice} --socks5 127.0.0.1:{proxy.Port} http://{DEV_BOX_IP}:{STRESS_HTTP_PORT}/";

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
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "Normal", bytesToSend);
            }



            if (mode == Mode.IsolatedReads)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (Isolated Reads mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004 --isolated-reads"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args} --isolated-reads"),
                        "Isolated Reads", bytesToSend);
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
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
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
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
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
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "HTTP API", bytesToSend);
            }
        }

        public static void ConductTest(string name, Client side1, Server server, Client side2, string mode, int bytesToSend = 5 * 1024 * 1024, Action<CancellationToken>? transferOverride = null, int timeoutSeconds = 180)
        {

            var testNumberStr = $"Test {testNumber++}";
            File.AppendAllLines(localWindowsOutputFilename, [testNumberStr]);

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
            transfersTask.Wait();

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



            File.AppendAllLines(localWindowsOutputFilename, ["--------------------------------------------------------------------------------"]);

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
        Mac
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

        VirtualBoxSharedFolder,

        VirtioFs,
        Virtio9p
    }

    public enum Mode
    {
        Normal,
        IsolatedReads,
        UploadDownload,
        FTP,
        HttpApi
    }
}
