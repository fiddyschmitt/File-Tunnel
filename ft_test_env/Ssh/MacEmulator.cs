using ft_test_env.Config;
using ft_test_env.Steps;
using Renci.SshNet;

namespace ft_test_env.Ssh
{
    /// <summary>
    /// Configures + launches the Android emulator on the Mac (.33) over SSH (key auth), for the ft Android
    /// (linux-bionic-arm64) e2e client rows (issue #45). The Mac is not VBox/cloud-init managed, so everything is
    /// driven over SSH: stage + run mac_android_setup.sh (installs the SDK + AVD into ~/Library/Android/sdk), launch
    /// the emulator headless, poll for boot, and tear it down. adb + the emulator run ON the Mac; the adb serial is
    /// emulator-&lt;port&gt;. Uses the same 'smith' key auth as the Mac ft_tests runners.
    /// </summary>
    public class MacEmulator
    {
        private readonly MacEmulatorConfig cfg;
        private readonly string setupScriptPath;
        private readonly string adb;        // "/Users/<user>/Library/Android/sdk/platform-tools/adb"
        private readonly string emulator;   // "…/emulator/emulator"

        public MacEmulator(MacEmulatorConfig cfg)
        {
            this.cfg = cfg;
            setupScriptPath = Path.Combine(AppContext.BaseDirectory, "Cloud", "mac_android_setup.sh");
            var sdk = $"/Users/{cfg.Username}/Library/Android/sdk";
            adb = $"{sdk}/platform-tools/adb";
            emulator = $"{sdk}/emulator/emulator";
        }

        private ConnectionInfo Conn()
        {
            var key = new PrivateKeyFile(cfg.ResolvedKeyPath);
            return new ConnectionInfo(cfg.Host, cfg.SshPort, cfg.Username, new PrivateKeyAuthenticationMethod(cfg.Username, key));
        }

        /// <summary>Stages + runs mac_android_setup.sh on the Mac (installs cmdline-tools/platform-tools/emulator/
        /// system-image + creates the AVD). Idempotent; the big downloads happen only on first run.</summary>
        public StepOutcome Setup()
        {
            if (!File.Exists(setupScriptPath))
                return StepOutcome.Fail($"setup script not found: {setupScriptPath}");

            using var ssh = new SshClient(Conn());
            ssh.Connect();

            // Stage the script base64-encoded to dodge SCP + C#->SSH->shell quoting, then run it.
            var b64 = Convert.ToBase64String(File.ReadAllBytes(setupScriptPath));
            ssh.CreateCommand($"echo {b64} | base64 -d > ~/mac_android_setup.sh && chmod +x ~/mac_android_setup.sh").Execute();

            using var cmd = ssh.CreateCommand($"bash ~/mac_android_setup.sh '{cfg.AvdName}' '{cfg.SystemImage}' 2>&1");
            cmd.CommandTimeout = TimeSpan.FromSeconds(cfg.SetupTimeoutSeconds);
            var output = cmd.Execute();
            return output.Contains("SETUP_DONE", StringComparison.Ordinal)
                ? StepOutcome.Ok($"SDK + AVD '{cfg.AvdName}' ready")
                : StepOutcome.Fail($"setup did not finish: {LastLine(output)}");
        }

        /// <summary>Kills any prior emulator on the port, launches this AVD headless, and waits for it to boot.</summary>
        public StepOutcome Launch()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();

            // Clear a prior instance on this port, then (re)start the adb server.
            ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} emu kill 2>/dev/null; true").Execute();
            ssh.CreateCommand($"\"{adb}\" start-server >/dev/null 2>&1; true").Execute();

            // Headless launch, detached so it survives the SSH channel closing (nohup + & + </dev/null → init).
            var launch = $"nohup \"{emulator}\" -avd {cfg.AvdName} -no-window -no-audio -no-boot-anim -no-snapshot " +
                         $"-gpu swiftshader_indirect -port {cfg.EmulatorPort} -no-metrics >~/ft_emulator.log 2>&1 </dev/null &";
            ssh.CreateCommand(launch).Execute();

            // Poll for a fully-booted device.
            var deadline = DateTime.UtcNow.AddSeconds(cfg.BootTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var booted = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} shell getprop sys.boot_completed 2>/dev/null").Execute().Trim();
                if (booted == "1")
                {
                    var abi = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} shell getprop ro.product.cpu.abi 2>/dev/null").Execute().Trim();
                    return StepOutcome.Ok($"{cfg.Serial} booted ({abi})");
                }
                Thread.Sleep(4000);
            }
            return StepOutcome.Fail($"{cfg.Serial} did not boot within {cfg.BootTimeoutSeconds}s (see ~/ft_emulator.log on {cfg.Host})");
        }

        /// <summary>Kills the emulator if it is running.</summary>
        public StepOutcome Teardown()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var state = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} get-state 2>&1").Execute().Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                return StepOutcome.Skip("not running");
            ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} emu kill 2>/dev/null; true").Execute();
            return StepOutcome.Ok("emulator killed");
        }

        /// <summary>Reports whether the emulator is up + fully booted.</summary>
        public StepOutcome Check()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var state = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} get-state 2>&1").Execute().Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                return StepOutcome.Fail($"{cfg.Serial} not available (adb get-state = '{state}')");
            var booted = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} shell getprop sys.boot_completed 2>/dev/null").Execute().Trim();
            var abi = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} shell getprop ro.product.cpu.abi 2>/dev/null").Execute().Trim();
            return booted == "1"
                ? StepOutcome.Ok($"{cfg.Serial} up + booted ({abi})")
                : StepOutcome.Fail($"{cfg.Serial} present but not booted (boot_completed='{booted}')");
        }

        private static string LastLine(string s)
        {
            var lines = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[^1] : "(no output)";
        }
    }
}
