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
    // Android is a tunnel CLIENT only, and specifically side2 (client2). The emulator's user-mode NAT gives it
    // OUTBOUND to the LAN (so it reaches the WebDav/S3 backend on .81) but no INBOUND - and in the HttpApi
    // topology the harness only ever connects to side1 while side2 just talks to the backend, so side2 needs no
    // inbound. Deploy is two hops: SCP the binary dev-box -> Mac (like MacProcessRunner), then `adb push` the
    // staged copy Mac -> emulator.
    public class AndroidProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;           // SSH to the Mac (.33)
        private readonly ConnectionInfo connectionInfo;
        private readonly string adb;                     // "<adbPath>" -s <serial>  (all adb runs on the Mac)
        private readonly string remoteExecutablePath;    // on the emulator: /data/local/tmp/ft-<instance>
        private readonly string outputFilename;          // on the emulator

        public AndroidProcessRunner(string macHost, string username, string privateKeyPath, string localExecutablePath,
                                    string adbPath, string serial, int instance = 1, int port = 22) : base(macHost)
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
            Stop();
        }

        public override void Run(string args)
        {
            Stop();
            // Launch ft inside the emulator, detached so it outlives the adb shell: nohup + background +
            // </dev/null reparents it to init (verified: survives adb's disconnect). The single quotes keep the
            // ft args - which contain double-quoted object names - intact through Mac shell -> adb -> device shell.
            var deviceCmd = $"cd /data/local/tmp; TMPDIR=/data/local/tmp nohup {remoteExecutablePath} {args} >{outputFilename} 2>&1 </dev/null &";
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
