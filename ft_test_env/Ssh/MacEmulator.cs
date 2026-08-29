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

        /// <summary>Ensures BOTH emulators are up (emu1 bridged, emu2 plain NAT), roots adbd on both, and returns
        /// emu1's bridged LAN IP. IDEMPOTENT: if both are already booted it leaves them alone (only kills + relaunches
        /// when they are not), so re-running BringUpAll doesn't disrupt working emulators. Two REAL emulators are the two Android tunnel
        /// clients: emu1 (bridged, real LAN IP) is client1/side1 - the only role needing inbound reachability - and
        /// emu2 (NAT, outbound-only) is client2/side2. macOS vmnet can't give a SECOND concurrent bridged emulator a
        /// working default network (Android's connectivity framework only makes wlan0 the default on the first), so
        /// emu2 is left on the normal user-mode NAT, whose default network reaches the LAN outbound - all side2 needs.</summary>
        public StepOutcome Launch()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();

            // The whole launch + boot-wait + LAN-IP discovery runs as ONE staged bash script over ONE SSH channel:
            // the emulators only register with adb if the session stays open through their early registration (found
            // the hard way - separate SSH.NET channels each close and leave them started-but-unregistered).
            // Teardown must reap the qemu CHILD (its cmdline carries `-port <p>`; killing only the `emulator` launcher
            // orphans it and holds the port), then reset the adb server to drop any stale registration. -read-only
            // uses a throwaway overlay (so two emulators can share one AVD, and userdata is never root-owned).
            var u = cfg.Username;
            var p1 = cfg.EmulatorPort;         // emu1: bridged
            var p2 = cfg.SecondEmulatorPort;   // emu2: NAT
            var envs = $"HOME=/Users/{u} ANDROID_AVD_HOME=/Users/{u}/.android/avd ANDROID_SDK_ROOT=/Users/{u}/Library/Android/sdk ANDROID_EMULATOR_HOME=/Users/{u}/.android";
            var common = "-no-window -no-audio -no-boot-anim -read-only -gpu swiftshader_indirect -no-metrics";
            var script = string.Join("\n",
                "#!/bin/bash",
                $"ADB=\"{adb}\"; EMU=\"{emulator}\"; S1=emulator-{p1}; S2=emulator-{p2}",
                // booted(serial): true iff it is a fully-booted adb device (get-state=device AND boot_completed=1).
                // tr -dc 0-9 strips any adb warning text so the compare is exact, never a stray value.
                "booted() { [ \"$(\"$ADB\" -s \"$1\" get-state 2>/dev/null)\" = device ] && [ \"$(\"$ADB\" -s \"$1\" shell getprop sys.boot_completed 2>/dev/null | tr -dc 0-9)\" = 1 ]; }",
                // IDEMPOTENT: only kill + relaunch if they are NOT both already up, so re-running the bring-up (the
                // Launch is folded into BringUpAll) doesn't tear down working emulators.
                "if booted \"$S1\" && booted \"$S2\"; then echo already-up; else",
                "  \"$ADB\" -s \"$S1\" emu kill 2>/dev/null; \"$ADB\" -s \"$S2\" emu kill 2>/dev/null; sleep 2",
                $"  sudo pkill -f \"qemu-system.*-port {p1}\" 2>/dev/null; sudo pkill -f \"qemu-system.*-port {p2}\" 2>/dev/null",
                $"  sudo pkill -f \"emulator.*-port {p1}\" 2>/dev/null; sudo pkill -f \"emulator.*-port {p2}\" 2>/dev/null; sleep 2",
                "  \"$ADB\" kill-server >/dev/null 2>&1; sleep 1; \"$ADB\" start-server >/dev/null 2>&1; sleep 2",
                // emu1: BRIDGED (sudo, for vmnet) -> real LAN IP, reachable inbound (client1/side1).
                $"  sudo {envs} \\",
                $"    \"$EMU\" -avd {cfg.AvdName} {common} -port {p1} -vmnet-bridged {cfg.BridgeInterface} >~/ft_emulator.log 2>&1 &",
                // emu2: PLAIN NAT (no sudo, no bridge) -> normal working default network, outbound to the LAN (client2/side2).
                $"  {envs} \\",
                $"    nohup \"$EMU\" -avd {cfg.AvdName} {common} -port {p2} >~/ft_emulator2.log 2>&1 &",
                "  for i in $(seq 1 90); do booted \"$S1\" && booted \"$S2\" && break; sleep 4; done",
                "fi",
                // Root adbd on both (userdebug): sshfs rows (issue #45) need root for SELinux-permissive + the FUSE
                // mount, and ft then runs as root. adb root drops+reopens the connection, so wait for each device.
                "\"$ADB\" -s \"$S1\" root >/dev/null 2>&1 || true; \"$ADB\" -s \"$S1\" wait-for-device",
                "\"$ADB\" -s \"$S2\" root >/dev/null 2>&1 || true; \"$ADB\" -s \"$S2\" wait-for-device",
                // Final state - b1/b2 are ALWAYS 0 or 1 (never empty), so the RESULT line parses cleanly.
                "b1=0; booted \"$S1\" && b1=1; b2=0; booted \"$S2\" && b2=1",
                // emu1's bridged LAN IP (DHCP lands a bit after boot); emu2 has none (NAT).
                "ip1=''; for i in $(seq 1 45); do ip1=$(\"$ADB\" -s \"$S1\" shell ip -4 addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}'); [ -n \"$ip1\" ] && break; sleep 3; done",
                "abi=$(\"$ADB\" -s \"$S1\" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\\r ')",
                "echo \"RESULT boot1=$b1 boot2=$b2 ip1=$ip1 abi=$abi\"");
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            using var cmd = ssh.CreateCommand($"echo {b64} | base64 -d | bash");
            cmd.CommandTimeout = TimeSpan.FromSeconds(cfg.BootTimeoutSeconds + 420);
            var output = cmd.Execute();

            var m = System.Text.RegularExpressions.Regex.Match(output, @"RESULT boot1=(\S*) boot2=(\S*) ip1=(\S*) abi=(\S*)");
            var boot1 = m.Success && m.Groups[1].Value == "1";
            var boot2 = m.Success && m.Groups[2].Value == "1";
            var lanIp = m.Success ? m.Groups[3].Value : "";
            if (boot1 && boot2 && !string.IsNullOrEmpty(lanIp))
                return StepOutcome.Ok($"{cfg.Serial} (bridged {lanIp}, {m.Groups[4].Value}) + {cfg.SecondSerial} (NAT) booted");
            if (!boot1 || !boot2)
                return StepOutcome.Fail($"emulator(s) did not boot (boot1={m.Groups[1].Value} boot2={m.Groups[2].Value}); see ~/ft_emulator*.log on {cfg.Host}");
            return StepOutcome.Fail($"{cfg.Serial} booted but no bridged LAN IP - bridge '{cfg.BridgeInterface}' failed (see ~/ft_emulator.log on {cfg.Host})");
        }

        /// <summary>Kills both emulators if running (reaping the qemu children so no orphan holds a console port).</summary>
        public StepOutcome Teardown()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var script = string.Join("\n",
                $"\"{adb}\" -s {cfg.Serial} emu kill 2>/dev/null; \"{adb}\" -s {cfg.SecondSerial} emu kill 2>/dev/null; sleep 2",
                $"sudo pkill -f \"qemu-system.*-port {cfg.EmulatorPort}\" 2>/dev/null; sudo pkill -f \"qemu-system.*-port {cfg.SecondEmulatorPort}\" 2>/dev/null",
                $"sudo pkill -f \"emulator.*-port {cfg.EmulatorPort}\" 2>/dev/null; sudo pkill -f \"emulator.*-port {cfg.SecondEmulatorPort}\" 2>/dev/null; true");
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            ssh.CreateCommand($"echo {b64} | base64 -d | bash").Execute();
            return StepOutcome.Ok("emulators killed");
        }

        /// <summary>Reports whether BOTH emulators are up + fully booted (emu1 bridged with a LAN IP, emu2 on NAT).</summary>
        public StepOutcome Check()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var (ok1, msg1) = CheckOne(ssh, cfg.Serial, bridged: true);
            var (ok2, msg2) = CheckOne(ssh, cfg.SecondSerial, bridged: false);
            return (ok1 && ok2) ? StepOutcome.Ok($"{msg1}; {msg2}") : StepOutcome.Fail($"{msg1}; {msg2}");
        }

        private (bool ok, string msg) CheckOne(SshClient ssh, string serial, bool bridged)
        {
            var state = ssh.CreateCommand($"\"{adb}\" -s {serial} get-state 2>&1").Execute().Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                return (false, $"{serial} not available (get-state='{state}')");
            var booted = ssh.CreateCommand($"\"{adb}\" -s {serial} shell getprop sys.boot_completed 2>/dev/null").Execute().Trim();
            if (booted != "1")
                return (false, $"{serial} present but not booted");
            var abi = ssh.CreateCommand($"\"{adb}\" -s {serial} shell getprop ro.product.cpu.abi 2>/dev/null").Execute().Trim();
            return bridged
                ? (true, $"{serial} booted ({abi}), bridged LAN IP {WlanIp(ssh, serial) ?? "MISSING"}")
                : (true, $"{serial} booted ({abi}), NAT");
        }

        /// <summary>The bridged emulator's real LAN IP (wlan0). eth0 stays the user-mode NAT 10.0.2.15.</summary>
        private string? WlanIp(SshClient ssh, string serial)
        {
            var raw = ssh.CreateCommand($"\"{adb}\" -s {serial} shell ip -4 addr show wlan0 2>/dev/null").Execute();
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
