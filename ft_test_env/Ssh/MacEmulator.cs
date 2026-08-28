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

            // Run the whole launch + boot-wait + LAN-IP discovery as ONE staged bash script over ONE SSH channel.
            // The bridged emulator runs as root (sudo, needed for vmnet) and only registers with adb if the session
            // stays open through its early registration - doing the launch and the boot-poll in SEPARATE SSH.NET
            // channels (each closing after Execute) leaves it started-but-never-registered (found the hard way; the
            // manual OpenSSH run kept one session open). -vmnet-bridged puts wlan0 on a REAL LAN IP via DHCP, so the
            // emulator is reachable inbound like any node (what lets Android be a tunnel side1, not only side2);
            // -read-only uses a throwaway overlay so the AVD's userdata is never root-owned. wlan0's DHCP lands a bit
            // AFTER sys.boot_completed, so the IP is polled too.
            var u = cfg.Username;
            var p = cfg.EmulatorPort;
            var script = string.Join("\n",
                "#!/bin/bash",
                $"ADB=\"{adb}\"; EMU=\"{emulator}\"; S=emulator-{p}",
                "\"$ADB\" -s \"$S\" emu kill 2>/dev/null; sleep 2",
                $"sudo pkill -f \"emulator.*-port {p}\" 2>/dev/null; sleep 2",
                "\"$ADB\" start-server >/dev/null 2>&1",
                $"sudo HOME=/Users/{u} ANDROID_AVD_HOME=/Users/{u}/.android/avd ANDROID_SDK_ROOT=/Users/{u}/Library/Android/sdk ANDROID_EMULATOR_HOME=/Users/{u}/.android \\",
                $"  \"$EMU\" -avd {cfg.AvdName} -no-window -no-audio -no-boot-anim -read-only -gpu swiftshader_indirect -port {p} -no-metrics -vmnet-bridged {cfg.BridgeInterface} >~/ft_emulator.log 2>&1 &",
                "b=0; for i in $(seq 1 70); do st=$(\"$ADB\" -s \"$S\" get-state 2>/dev/null); [ \"$st\" = device ] && b=$(\"$ADB\" -s \"$S\" shell getprop sys.boot_completed 2>/dev/null | tr -d '\\r'); [ \"$b\" = 1 ] && break; sleep 4; done",
                "ip=''; for i in $(seq 1 45); do ip=$(\"$ADB\" -s \"$S\" shell ip -4 addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}'); [ -n \"$ip\" ] && break; sleep 3; done",
                "abi=$(\"$ADB\" -s \"$S\" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\\r ')",
                "echo \"RESULT boot=$b ip=$ip abi=$abi\"");
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            using var cmd = ssh.CreateCommand($"echo {b64} | base64 -d | bash");
            cmd.CommandTimeout = TimeSpan.FromSeconds(cfg.BootTimeoutSeconds + 240);
            var output = cmd.Execute();

            var m = System.Text.RegularExpressions.Regex.Match(output, @"RESULT boot=(\S*) ip=(\S*) abi=(\S*)");
            var bootOk = m.Success && m.Groups[1].Value == "1";
            var lanIp = m.Success ? m.Groups[2].Value : "";
            if (bootOk && !string.IsNullOrEmpty(lanIp))
                return StepOutcome.Ok($"{cfg.Serial} booted ({m.Groups[3].Value}), bridged LAN IP {lanIp}");
            if (bootOk)
                return StepOutcome.Fail($"{cfg.Serial} booted but wlan0 has NO LAN IP - bridge '{cfg.BridgeInterface}' failed (see ~/ft_emulator.log on {cfg.Host})");
            return StepOutcome.Fail($"{cfg.Serial} did not boot (see ~/ft_emulator.log on {cfg.Host})");
        }

        /// <summary>Kills the emulator if it is running.</summary>
        public StepOutcome Teardown()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var state = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} get-state 2>&1").Execute().Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                return StepOutcome.Skip("not running");
            // The bridged emulator runs as root (sudo), so fall back to a sudo pkill if `emu kill` does not take.
            ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} emu kill 2>/dev/null; sleep 2; sudo pkill -f \"emulator.*-port {cfg.EmulatorPort}\" 2>/dev/null; true").Execute();
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
                ? StepOutcome.Ok($"{cfg.Serial} up + booted ({abi}), bridged LAN IP {WlanIp(ssh) ?? "MISSING"}")
                : StepOutcome.Fail($"{cfg.Serial} present but not booted (boot_completed='{booted}')");
        }

        /// <summary>The bridged emulator's real LAN IP (wlan0). eth0 stays the user-mode NAT 10.0.2.15.</summary>
        private string? WlanIp(SshClient ssh)
        {
            var raw = ssh.CreateCommand($"\"{adb}\" -s {cfg.Serial} shell ip -4 addr show wlan0 2>/dev/null").Execute();
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"inet (\d+\.\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string LastLine(string s)
        {
            var lines = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[^1] : "(no output)";
        }
    }
}
