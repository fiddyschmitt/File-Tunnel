using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    // An Android emulator (running on the Mac .33), driven over SSH + adb. ft here is the NativeAOT
    // linux-bionic-arm64 build (issue #45) - a native Android/Bionic binary - pushed into the emulator's
    // /data/local/tmp and launched with `adb shell`. The emulator is stood up by ft_test_env; if it is not
    // running, the constructor throws and the runner is treated as unavailable (Android rows Assert.Inconclusive),
    // exactly like a down Windows VM.
    //
    // Android is a tunnel CLIENT (both client1/side1 and client2/side2). The emulator is launched BRIDGED by
    // ft_test_env (-vmnet-bridged), so its wlan0 gets a real LAN IP (DiscoverLanIp -> RunOnIP) and it is reachable
    // inbound like any node - which is what lets it be side1, not just side2 (its user-mode eth0 NAT alone would
    // give outbound but no inbound). Deploy is two hops: SCP the binary dev-box -> Mac (like MacProcessRunner),
    // then `adb push` the staged copy Mac -> emulator.
    public class AndroidProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;           // SSH to the Mac (.33)
        private readonly ConnectionInfo connectionInfo;
        private readonly string adb;                     // "<adbPath>" -s <serial>  (all adb runs on the Mac)
        private readonly string remoteExecutablePath;    // on the emulator: /data/local/tmp/ft-<instance>
        private readonly string outputFilename;          // on the emulator

        public AndroidProcessRunner(string macHost, string username, string privateKeyPath, string localExecutablePath,
                                    string adbPath, string serial, int instance = 1, int port = 22)
            : base(DiscoverLanIp(macHost, username, privateKeyPath, adbPath, serial, port) ?? macHost)
        {
            var keyFile = new PrivateKeyFile(privateKeyPath);
            connectionInfo = new ConnectionInfo(macHost, port, username, new PrivateKeyAuthenticationMethod(username, keyFile));
            sshClient = new SshClient(connectionInfo);
            sshClient.Connect();

            adb = $"\"{adbPath}\" -s {serial}";
            remoteExecutablePath = $"/data/local/tmp/ft-{instance}";
            outputFilename = $"/data/local/tmp/ft-{instance}.log";

            // Fail fast if the emulator is not up, so a row that needs it is skipped rather than hanging.
            var state = sshClient.CreateCommand($"{adb} get-state 2>&1").Execute().Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                throw new InvalidOperationException($"Android emulator {serial} not ready on {macHost}: adb get-state = '{state}'");

            // Two-hop deploy: dev-box binary -> Mac staging (scp) -> emulator (adb push).
            var macStaging = $"/tmp/ft-android/{Path.GetFileName(localExecutablePath)}-{instance}";
            sshClient.CreateCommand("mkdir -p /tmp/ft-android").Execute();
            using (var scp = new ScpClient(connectionInfo))
            {
                scp.Connect();
                scp.Upload(new FileInfo(localExecutablePath), macStaging);
            }
            sshClient.CreateCommand($"{adb} push \"{macStaging}\" \"{remoteExecutablePath}\"").Execute();
            sshClient.CreateCommand($"{adb} shell chmod 755 \"{remoteExecutablePath}\"").Execute();

            // Push the bundled Bionic OpenSSL (staged on the Mac by mac_android_setup.sh) into /data/local/tmp/ssl,
            // which Run() puts on LD_LIBRARY_PATH so ft's crypto backends (S3 SigV4, HTTPS/Dropbox) use real OpenSSL
            // instead of Android's BoringSSL. Best-effort: if the libs are absent, only the crypto rows are affected.
            var osslMac = $"/Users/{username}/Library/Android/ft-openssl";
            sshClient.CreateCommand($"{adb} shell 'mkdir -p /data/local/tmp/ssl'").Execute();
            sshClient.CreateCommand($"{adb} push \"{osslMac}/libcrypto.so\" /data/local/tmp/ssl/libcrypto.so 2>/dev/null; {adb} push \"{osslMac}/libssl.so\" /data/local/tmp/ssl/libssl.so 2>/dev/null; true").Execute();

            Stop();
        }

        // The emulator, launched bridged (ft_test_env: -vmnet-bridged), gets a REAL LAN IP on wlan0 via DHCP
        // (eth0 stays the user-mode NAT 10.0.2.15). That LAN IP becomes this runner's RunOnIP, so the harness dials
        // the emulator directly - letting Android be side1 (client1) like any node. Falls back to the Mac's IP if
        // wlan0 has no LAN IP (a non-bridged emulator), which is harmless for a side2-only Android.
        private static string? DiscoverLanIp(string macHost, string username, string keyPath, string adbPath, string serial, int port)
        {
            try
            {
                var key = new PrivateKeyFile(keyPath);
                using var ssh = new SshClient(new ConnectionInfo(macHost, port, username, new PrivateKeyAuthenticationMethod(username, key)));
                ssh.Connect();
                var raw = ssh.CreateCommand($"\"{adbPath}\" -s {serial} shell ip -4 addr show wlan0 2>/dev/null").Execute();
                var m = System.Text.RegularExpressions.Regex.Match(raw, @"inet (\d+\.\d+\.\d+\.\d+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public override void Run(string args)
        {
            Stop();
            // Launch ft inside the emulator, detached so it outlives the adb shell: nohup + background +
            // </dev/null reparents it to init (verified: survives adb's disconnect). The single quotes keep the
            // ft args - which contain double-quoted object names - intact through Mac shell -> adb -> device shell.
            // LD_LIBRARY_PATH=/data/local/tmp/ssl makes ft load the BUNDLED Bionic OpenSSL (libssl.so /
            // libcrypto.so, unversioned - .NET's linux-bionic crypto shim probes the unversioned soname)
            // instead of Android's BoringSSL, so crypto backends (S3 SigV4, HTTPS/Dropbox) work. Harmless for
            // the plaintext backends (WebDav/FTP). The libs are pushed there by the emulator provisioning.
            var deviceCmd = $"cd /data/local/tmp; TMPDIR=/data/local/tmp LD_LIBRARY_PATH=/data/local/tmp/ssl nohup {remoteExecutablePath} {args} >{outputFilename} 2>&1 </dev/null &";
            var command = $"{adb} shell '{deviceCmd}'";
            Debug.WriteLine(command);
            sshClient.CreateCommand(command).Execute();
        }

        public override string GetFullCommand(string args) => $"{adb} shell '{remoteExecutablePath} {args}'";

        public override TimeSpan? Stop()
        {
            // Kill only THIS instance's ft on the device, matched by its unique path.
            sshClient.CreateCommand($"{adb} shell 'pkill -f {remoteExecutablePath} || true'").Execute();
            return null;
        }

        public override void DeleteFile(string path)
        {
            sshClient.CreateCommand($"{adb} shell 'rm -f \"{path}\" || true'").Execute();
        }

        public override void Run(string cmd, string args)
        {
            sshClient.CreateCommand($"{adb} shell '{cmd} {args}'").Execute();
        }

        public override (int ExitCode, string Output) RunCommand(string command)
        {
            // Run a command INSIDE the emulator and block for its combined output.
            using var sshCommand = sshClient.CreateCommand($"{adb} shell '{command}'");
            var stdout = sshCommand.Execute();
            return (sshCommand.ExitStatus ?? -1, stdout + sshCommand.Error);
        }
    }
}
