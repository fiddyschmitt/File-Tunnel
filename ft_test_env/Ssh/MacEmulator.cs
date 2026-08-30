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

        /// <summary>Ensures BOTH emulators are up as BRIDGED nodes (each a real LAN IP), roots adbd on both, and
        /// returns their state. HEALTH-AWARE + IDEMPOTENT: it leaves the pair alone only if both are booted AND healthy
        /// (emu2 can reach emu1) - so re-running BringUpAll doesn't disrupt a working pair, but a degraded-but-booted
        /// pair (networking gone stale after a lab restart / long idle - which a pure `booted` check would miss) is
        /// force-relaunched. Both emulators are BRIDGED (real LAN IPs) so every tunnel role - and FTP's data channel -
        /// works over either; they are launched SEQUENTIALLY (emu1 boots + DHCPs first, then emu2). An earlier note here
        /// claimed macOS vmnet can't give a 2nd concurrent bridged emulator a default route - that was a SIMULTANEOUS-
        /// launch race (Android promotes wlan0 to the default network on only the first); launched sequentially, the
        /// 2nd emulator gets a proper default route too (verified: 2nd bridged emulator got a real LAN IP + default
        /// route + outbound reachability). client1/side1 = emu1, client2/side2 = emu2, both now with real LAN IPs.</summary>
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
            var p2 = cfg.SecondEmulatorPort;   // emu2: bridged (sequential launch)
            var iface = cfg.BridgeInterface;   // vmnet bridge interface (en0)
            var envs = $"HOME=/Users/{u} ANDROID_AVD_HOME=/Users/{u}/.android/avd ANDROID_SDK_ROOT=/Users/{u}/Library/Android/sdk ANDROID_EMULATOR_HOME=/Users/{u}/.android";
            var common = "-no-window -no-audio -no-boot-anim -read-only -gpu swiftshader_indirect -no-metrics";
            var script = string.Join("\n",
                "#!/bin/bash",
                $"ADB=\"{adb}\"; EMU=\"{emulator}\"; S1=emulator-{p1}; S2=emulator-{p2}",
                // booted(serial): true iff it is a fully-booted adb device (get-state=device AND boot_completed=1).
                "booted() { [ \"$(\"$ADB\" -s \"$1\" get-state 2>/dev/null)\" = device ] && [ \"$(\"$ADB\" -s \"$1\" shell getprop sys.boot_completed 2>/dev/null | tr -dc 0-9)\" = 1 ]; }",
                // wlanip(serial): the emulator's bridged LAN IP (wlan0); empty until DHCP lands.
                "wlanip() { \"$ADB\" -s \"$1\" shell ip -4 addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}'; }",
                // reaches(src ip): 0 if src reaches ip:22 FAST (RST/connect), 1 if it blackhole-hangs. This is the
                // health signal - a stale/degraded emulator (e.g. after a lab restart or long idle) hangs here, while
                // a `booted` check alone would call it fine. :22 has no service, so a reachable peer answers with a
                // fast RST; timing (<3s vs nc's -w4) separates that from a blackhole.
                "reaches() { local d=\"$2\"; [ -z \"$d\" ] && return 1; local t=$SECONDS; \"$ADB\" -s \"$1\" shell \"toybox nc -w4 $d 22 </dev/null\" >/dev/null 2>&1; [ $((SECONDS-t)) -lt 3 ]; }",
                "waitboot() { local i; for i in $(seq 1 90); do booted \"$1\" && return 0; sleep 4; done; return 1; }",
                "waitip() { local i x=''; for i in $(seq 1 45); do x=$(wlanip \"$1\"); [ -n \"$x\" ] && { echo \"$x\"; return 0; }; sleep 3; done; return 1; }",
                // launch1(port logfile): a BRIDGED emulator (sudo, for vmnet -> real LAN IP). The env assignments are
                // literal tokens here (C#-interpolated) so sudo applies them; a bare "$envs cmd" would NOT expand them.
                $"launch1() {{ sudo {envs} \"$EMU\" -avd {cfg.AvdName} {common} -port \"$1\" -vmnet-bridged {iface} >\"$2\" 2>&1 & }}",
                // HEALTH-AWARE IDEMPOTENCE: leave the pair alone only if BOTH are booted AND healthy (emu2 reaches
                // emu1) - so re-running the bring-up doesn't disrupt a working pair, but a degraded-but-booted pair
                // (stale networking) gets refreshed, which a pure `booted` check misses.
                // lanip(ip): 0 if it is a real LAN IP (bridged), 1 for empty or the 10.0.x emulator NAT range - so a
                // NAT emu2 (or a bridge that raced and fell back) counts as unhealthy and gets relaunched.
                "lanip() { case \"$1\" in ''|10.0.*) return 1;; *) return 0;; esac; }",
                "RELAUNCH=1",
                "if booted \"$S1\" && booted \"$S2\"; then ip1=$(wlanip \"$S1\"); ip2=$(wlanip \"$S2\"); if lanip \"$ip1\" && lanip \"$ip2\" && reaches \"$S2\" \"$ip1\"; then RELAUNCH=0; echo already-up-healthy; fi; fi",
                "if [ \"$RELAUNCH\" = 1 ]; then",
                "  echo relaunching",
                "  \"$ADB\" -s \"$S1\" emu kill 2>/dev/null; \"$ADB\" -s \"$S2\" emu kill 2>/dev/null; sleep 2",
                // SIGKILL (-9): `emu kill` acks ("bye bye") but the qemu child can linger past a plain SIGTERM pkill,
                // holding the console port so the fresh launch silently fails on a port conflict (found the hard way).
                $"  sudo pkill -9 -f \"qemu-system.*-port {p1}\" 2>/dev/null; sudo pkill -9 -f \"qemu-system.*-port {p2}\" 2>/dev/null",
                $"  sudo pkill -9 -f \"emulator.*-port {p1}\" 2>/dev/null; sudo pkill -9 -f \"emulator.*-port {p2}\" 2>/dev/null; sleep 3",
                "  \"$ADB\" kill-server >/dev/null 2>&1; sleep 1; \"$ADB\" start-server >/dev/null 2>&1; sleep 2",
                // SEQUENTIAL, both bridged: emu1 boots + DHCPs its LAN IP FIRST, THEN emu2 - so Android promotes wlan0
                // to the default network on EACH (a simultaneous bridged launch races and only the first gets a
                // default route; the loser is left with L2 but no route, which broke FTP's data channel over it).
                $"  launch1 {p1} ~/ft_emulator.log; waitboot \"$S1\"; ip1=$(waitip \"$S1\")",
                $"  launch1 {p2} ~/ft_emulator2.log; waitboot \"$S2\"; ip2=$(waitip \"$S2\")",
                "fi",
                // Root adbd on both (userdebug): sshfs rows (issue #45) need root for SELinux-permissive + the FUSE
                // mount, and ft then runs as root. adb root drops+reopens the connection, so wait for each device.
                "\"$ADB\" -s \"$S1\" root >/dev/null 2>&1 || true; \"$ADB\" -s \"$S1\" wait-for-device",
                "\"$ADB\" -s \"$S2\" root >/dev/null 2>&1 || true; \"$ADB\" -s \"$S2\" wait-for-device",
                // Final state - b1/b2 ALWAYS 0 or 1; BOTH emulators are bridged so both report a wlan0 LAN IP.
                "b1=0; booted \"$S1\" && b1=1; b2=0; booted \"$S2\" && b2=1",
                "ip1=$(wlanip \"$S1\"); ip2=$(wlanip \"$S2\")",
                "abi=$(\"$ADB\" -s \"$S1\" shell getprop ro.product.cpu.abi 2>/dev/null | tr -d '\\r ')",
                "echo \"RESULT boot1=$b1 boot2=$b2 ip1=$ip1 ip2=$ip2 abi=$abi\"");
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            using var cmd = ssh.CreateCommand($"echo {b64} | base64 -d | bash");
            cmd.CommandTimeout = TimeSpan.FromSeconds(cfg.BootTimeoutSeconds + 600);
            var output = cmd.Execute();

            var m = System.Text.RegularExpressions.Regex.Match(output, @"RESULT boot1=(\S*) boot2=(\S*) ip1=(\S*) ip2=(\S*) abi=(\S*)");
            var boot1 = m.Success && m.Groups[1].Value == "1";
            var boot2 = m.Success && m.Groups[2].Value == "1";
            var ip1 = m.Success ? m.Groups[3].Value : "";
            var ip2 = m.Success ? m.Groups[4].Value : "";
            var abi = m.Success ? m.Groups[5].Value : "";
            if (boot1 && boot2 && !string.IsNullOrEmpty(ip1) && !string.IsNullOrEmpty(ip2))
                return StepOutcome.Ok($"{cfg.Serial} (bridged {ip1}, {abi}) + {cfg.SecondSerial} (bridged {ip2}) booted");
            if (!boot1 || !boot2)
                return StepOutcome.Fail($"emulator(s) did not boot (boot1={m.Groups[1].Value} boot2={m.Groups[2].Value}); see ~/ft_emulator*.log on {cfg.Host}");
            return StepOutcome.Fail($"an emulator booted without a bridged LAN IP (ip1='{ip1}' ip2='{ip2}') - bridge '{cfg.BridgeInterface}' may have raced (see ~/ft_emulator*.log on {cfg.Host})");
        }

        /// <summary>Kills both emulators if running (reaping the qemu children so no orphan holds a console port).</summary>
        public StepOutcome Teardown()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var script = string.Join("\n",
                $"\"{adb}\" -s {cfg.Serial} emu kill 2>/dev/null; \"{adb}\" -s {cfg.SecondSerial} emu kill 2>/dev/null; sleep 2",
                $"sudo pkill -9 -f \"qemu-system.*-port {cfg.EmulatorPort}\" 2>/dev/null; sudo pkill -9 -f \"qemu-system.*-port {cfg.SecondEmulatorPort}\" 2>/dev/null",
                $"sudo pkill -9 -f \"emulator.*-port {cfg.EmulatorPort}\" 2>/dev/null; sudo pkill -9 -f \"emulator.*-port {cfg.SecondEmulatorPort}\" 2>/dev/null; true");
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            ssh.CreateCommand($"echo {b64} | base64 -d | bash").Execute();
            return StepOutcome.Ok("emulators killed");
        }

        /// <summary>Reports whether BOTH emulators are up + fully booted, each bridged with a real LAN IP.</summary>
        public StepOutcome Check()
        {
            using var ssh = new SshClient(Conn());
            ssh.Connect();
            var (ok1, msg1) = CheckOne(ssh, cfg.Serial, bridged: true);
            var (ok2, msg2) = CheckOne(ssh, cfg.SecondSerial, bridged: true);
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
