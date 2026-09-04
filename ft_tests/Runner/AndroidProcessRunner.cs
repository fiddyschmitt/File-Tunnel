using Renci.SshNet;
using System.Diagnostics;
using System.Text;

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
        private readonly int instance;
        private readonly string serial;
        private readonly string remoteExecutablePath;    // on the emulator: /data/local/tmp/ft-<instance>
        private readonly string outputFilename;          // on the emulator
        private readonly string sshfsPrefixMac;          // Termux sshfs prefix staged on the Mac by mac_android_setup.sh

        // The Termux prefix on the device: staging the sshfs toolchain here (its exact `pkg install`
        // location) makes the binaries' baked-in rpaths/config resolve, so sshfs/ssh "just work".
        private const string TermuxPrefixDevice = "/data/data/com.termux/files/usr";
        // Push the ~86MB toolchain only once per emulator session (the -read-only overlay resets on relaunch).
        private static readonly HashSet<string> sshfsToolchainReady = new();

        public AndroidProcessRunner(string macHost, string username, string privateKeyPath, string localExecutablePath,
                                    string adbPath, string serial, int instance = 1, int port = 22)
            : base(DiscoverLanIp(macHost, username, privateKeyPath, adbPath, serial, port) ?? macHost)
        {
            var keyFile = new PrivateKeyFile(privateKeyPath);
            connectionInfo = new ConnectionInfo(macHost, port, username, new PrivateKeyAuthenticationMethod(username, keyFile));
            sshClient = new SshClient(connectionInfo);
            sshClient.Connect();

            adb = $"\"{adbPath}\" -s {serial}";
            this.instance = instance;
            this.serial = serial;
            remoteExecutablePath = $"/data/local/tmp/ft-{instance}";
            outputFilename = $"/data/local/tmp/ft-{instance}.log";
            sshfsPrefixMac = $"/Users/{username}/Library/Android/ft-sshfs/usr";

            // Fail fast if the emulator is not up, so a row that needs it is skipped rather than hanging.
            var state = sshClient.ExecuteBounded($"{adb} get-state 2>&1").Trim();
            if (!state.EndsWith("device", StringComparison.Ordinal))
                throw new InvalidOperationException($"Android emulator {serial} not ready on {macHost}: adb get-state = '{state}'");

            // Two-hop deploy: dev-box binary -> Mac staging (scp) -> emulator (adb push).
            var macStaging = $"/tmp/ft-android/{Path.GetFileName(localExecutablePath)}-{instance}";
            sshClient.ExecuteBounded("mkdir -p /tmp/ft-android");
            using (var scp = new ScpClient(connectionInfo))
            {
                scp.Connect();
                scp.Upload(new FileInfo(localExecutablePath), macStaging);
            }
            sshClient.ExecuteBounded($"{adb} push \"{macStaging}\" \"{remoteExecutablePath}\"");
            sshClient.ExecuteBounded($"{adb} shell chmod 755 \"{remoteExecutablePath}\"");

            // Push the bundled Bionic OpenSSL (staged on the Mac by mac_android_setup.sh) into /data/local/tmp/ssl,
            // which Run() puts on LD_LIBRARY_PATH so ft's crypto backends (S3 SigV4, HTTPS/Dropbox) use real OpenSSL
            // instead of Android's BoringSSL. Best-effort: if the libs are absent, only the crypto rows are affected.
            var osslMac = $"/Users/{username}/Library/Android/ft-openssl";
            sshClient.ExecuteBounded($"{adb} shell 'mkdir -p /data/local/tmp/ssl'");
            sshClient.ExecuteBounded($"{adb} push \"{osslMac}/libcrypto.so\" /data/local/tmp/ssl/libcrypto.so 2>/dev/null; {adb} push \"{osslMac}/libssl.so\" /data/local/tmp/ssl/libssl.so 2>/dev/null; true");

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
                var raw = ssh.ExecuteBounded($"\"{adbPath}\" -s {serial} shell ip -4 addr show wlan0 2>/dev/null");
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
            // SSL_CERT_DIR points .NET/OpenSSL at Android's own 134-cert CA store so TLS cert validation works
            // (an HTTPS backend like Dropbox otherwise fails with "The SSL connection could not be established" -
            // the bundled OpenSSL ships no CA bundle). /system is read-only so this survives every -read-only relaunch.
            var deviceCmd = $"cd /data/local/tmp; TMPDIR=/data/local/tmp LD_LIBRARY_PATH=/data/local/tmp/ssl SSL_CERT_DIR=/system/etc/security/cacerts nohup {remoteExecutablePath} {args} >{outputFilename} 2>&1 </dev/null &";
            var command = $"{adb} shell '{deviceCmd}'";
            Debug.WriteLine(command);
            sshClient.ExecuteBounded(command);
        }

        public override string GetFullCommand(string args) => $"{adb} shell '{remoteExecutablePath} {args}'";

        public override TimeSpan? Stop()
        {
            // Kill only THIS instance's ft on the device, matched by its unique path.
            sshClient.ExecuteBounded($"{adb} shell 'pkill -f {remoteExecutablePath} || true'");
            return null;
        }

        public override void DeleteFile(string path)
        {
            sshClient.ExecuteBounded($"{adb} shell 'rm -f \"{path}\" || true'");
        }

        public override void Run(string cmd, string args)
        {
            sshClient.ExecuteBounded($"{adb} shell '{cmd} {args}'");
        }

        public override (int ExitCode, string Output) RunCommand(string command)
        {
            // Run a command INSIDE the emulator and block for its combined output.
            using var sshCommand = sshClient.CreateCommand($"{adb} shell '{command}'");
            sshCommand.CommandTimeout = SshExecuteExtensions.DefaultTimeout;
            string stdout;
            try { stdout = sshCommand.Execute(); }
            catch (Renci.SshNet.Common.SshOperationTimeoutException) { return (-1, "[ssh command timed out]"); }
            return (sshCommand.ExitStatus ?? -1, stdout + sshCommand.Error);
        }

        // ---- sshfs (issue #45): let the emulator be an sshfs client, exactly like a Termux user who runs
        // `pkg install sshfs`. The Termux sshfs toolchain (sshfs + fuse3 + openssh + openssl, staged on the Mac
        // by mac_android_setup.sh) is pushed to the device's Termux prefix, then sshfs mounts the lab export.
        // The resulting mount is a real FUSE filesystem (statfs f_type == 0x65735546), so bionic ft auto-enables
        // IsolatedIo over it just like on Linux. Used by AndroidSshfsClient.

        /// <summary>Push the Termux sshfs toolchain to the device once per emulator session (idempotent).</summary>
        public void EnsureSshfsToolchain()
        {
            lock (sshfsToolchainReady)
            {
                if (sshfsToolchainReady.Contains(serial)) return;
                // Ensure root adbd (userdebug image): the FUSE mount needs SELinux-permissive + the mount syscall.
                // `adb root` is idempotent - a no-op (no adbd restart) if already root, else it restarts adbd, so
                // wait for the device to reappear. MacEmulator.Launch also roots at boot; this covers other paths.
                sshClient.ExecuteBounded($"{adb} root");
                sshClient.ExecuteBounded($"{adb} wait-for-device");
                var present = sshClient.CreateCommand(
                    $"{adb} shell 'test -x {TermuxPrefixDevice}/bin/sshfs && echo READY'").Execute();
                if (!present.Contains("READY", StringComparison.Ordinal))
                {
                    sshClient.ExecuteBounded($"{adb} shell 'mkdir -p /data/data/com.termux/files'");
                    // adb push <macPrefix> <devicePrefix> lands the contents at the exact Termux prefix path.
                    var push = sshClient.CreateCommand($"{adb} push \"{sshfsPrefixMac}\" {TermuxPrefixDevice}");
                    push.CommandTimeout = TimeSpan.FromMinutes(3);
                    push.Execute();
                    var recheck = sshClient.CreateCommand(
                        $"{adb} shell 'test -x {TermuxPrefixDevice}/bin/sshfs && echo READY'").Execute();
                    if (!recheck.Contains("READY", StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"sshfs toolchain not staged on {serial}: is '{sshfsPrefixMac}' present on the Mac " +
                            $"(run mac_android_setup.sh / ft_test_env menu 10)? push said: {push.Result}");
                }
                sshfsToolchainReady.Add(serial);
            }
        }

        /// <summary>Mount an sshfs export at <paramref name="mountPoint"/> on the device (idempotent remount).
        /// Auth is password_stdin when <paramref name="password"/> is set, else key auth with the private key already
        /// on the device at <paramref name="deviceKeyPath"/> (push it first with <see cref="PushFile"/>). The server
        /// varies (Linux .81 / Mac .33 / Windows .84 / another emulator's Termux sshd on 8022) - hence the port.</summary>
        public void MountSshfs(string user, string server, string exportDir, string mountPoint,
                               int port = 22, string? password = null, string? deviceKeyPath = null)
        {
            // password_stdin is sshfs's reliable non-interactive password auth; for key auth, pin the key on both the
            // -o IdentityFile and the ssh_command. idmap=user maps the remote uid to root (which owns the mount);
            // ssh_command pins Termux's own ssh; StrictHostKeyChecking off avoids a first-connect prompt that hangs.
            var keyAuth = string.IsNullOrEmpty(password);
            var authOpt = keyAuth ? $"IdentityFile={deviceKeyPath}" : "password_stdin";
            var sshCmd = keyAuth ? $"$PREFIX/bin/ssh -i {deviceKeyPath} -p {port}" : $"$PREFIX/bin/ssh -p {port}";
            var feed = keyAuth ? "" : $"echo {password} | ";
            var script = string.Join("\n",
                $"PREFIX={TermuxPrefixDevice}",
                "export PATH=$PREFIX/bin:/system/bin:/system/xbin",
                "export LD_LIBRARY_PATH=$PREFIX/lib",
                "export HOME=/data/data/com.termux/files/home",
                "export TMPDIR=/data/local/tmp",
                $"mkdir -p $HOME {mountPoint}",
                "setenforce 0 2>/dev/null",             // FUSE mount from the su domain needs SELinux permissive
                $"fusermount3 -u {mountPoint} 2>/dev/null; umount -l {mountPoint} 2>/dev/null",
                $"{feed}sshfs {user}@{server}:{exportDir} {mountPoint} -p {port} " +
                $"-o {authOpt},StrictHostKeyChecking=no,UserKnownHostsFile=/dev/null,reconnect,ServerAliveInterval=15,idmap=user,ssh_command=\"{sshCmd}\"");
            RunDeviceScript(script, $"ft-sshfs-mount-{instance}");
        }

        /// <summary>Run Termux sshd on this emulator (Android2 = the sshfs server for the "direct" row): stage host
        /// keys + an authorized_keys (the pubkey text) + a config, and start sshd on 8022 with a local export dir.</summary>
        public void StartSshdServer(string authorizedKeyText, string exportDir, int port = 8022)
        {
            var script = string.Join("\n",
                $"PREFIX={TermuxPrefixDevice}",
                "export PATH=$PREFIX/bin:/system/bin:/system/xbin",
                "export LD_LIBRARY_PATH=$PREFIX/lib",
                "export HOME=/data/data/com.termux/files/home",
                "export TMPDIR=/data/local/tmp",
                "mkdir -p $HOME $PREFIX/etc/ssh $PREFIX/var/empty /data/local/tmp/sshd " + exportDir,
                "chmod 755 $PREFIX/var/empty",          // sshd privilege-separation dir
                "chmod 777 " + exportDir,
                "setenforce 0 2>/dev/null",
                // sshd rejects a login whose passwd shell does not exist; Termux openssh resolves root's shell to
                // $PREFIX/bin/bash, which the sshfs toolchain closure omits. sshfs only uses the sftp SUBSYSTEM (not
                // the login shell), so a symlink to the system sh is enough to get past the shell-exists check.
                "[ -e $PREFIX/bin/bash ] || ln -sf /system/bin/sh $PREFIX/bin/bash",
                "[ -f $PREFIX/etc/ssh/ssh_host_ed25519_key ] || $PREFIX/bin/ssh-keygen -A >/dev/null 2>&1",
                $"echo '{authorizedKeyText}' > /data/local/tmp/authorized_keys; chmod 600 /data/local/tmp/authorized_keys",
                "printf '%s\\n' " +
                    $"'Port {port}' 'ListenAddress 0.0.0.0' 'HostKey '$PREFIX'/etc/ssh/ssh_host_ed25519_key' " +
                    "'PidFile /data/local/tmp/sshd/sshd.pid' 'PermitRootLogin yes' 'PubkeyAuthentication yes' " +
                    "'PasswordAuthentication no' 'AuthorizedKeysFile /data/local/tmp/authorized_keys' 'StrictModes no' " +
                    "'Subsystem sftp '$PREFIX'/libexec/sftp-server' > /data/local/tmp/sshd_config",
                "pkill -f 'sshd -f /data/local/tmp/sshd_config' 2>/dev/null; sleep 1",
                "$PREFIX/bin/sshd -f /data/local/tmp/sshd_config",
                "sleep 1; echo sshd-started");
            RunDeviceScript(script, $"ft-sshd-{instance}");
        }

        /// <summary>SCP a local file to the Mac then adb-push it onto the device (mode 600) - used to place a private
        /// key on a client emulator for key-auth sshfs mounts.</summary>
        public void PushFile(string localPath, string devicePath)
        {
            var macStaging = $"/tmp/ft-android/{Path.GetFileName(localPath)}-{instance}";
            sshClient.ExecuteBounded("mkdir -p /tmp/ft-android");
            using (var scp = new ScpClient(connectionInfo)) { scp.Connect(); scp.Upload(new FileInfo(localPath), macStaging); }
            sshClient.ExecuteBounded($"{adb} push \"{macStaging}\" \"{devicePath}\"");
            sshClient.ExecuteBounded($"{adb} shell chmod 600 \"{devicePath}\"");
        }

        /// <summary>The bridged emulator's real LAN IP (wlan0) - where another emulator dials it for the "direct" row.</summary>
        public string? LanIp()
        {
            var raw = sshClient.ExecuteBounded($"{adb} shell ip -4 addr show wlan0 2>/dev/null");
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"inet (\d+\.\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Read a small file from the device (e.g. a generated public key).</summary>
        public string ReadDeviceFile(string devicePath) =>
            sshClient.ExecuteBounded($"{adb} shell cat \"{devicePath}\" 2>/dev/null").Trim();

        /// <summary>Stage a multi-line shell script on the device (base64, to dodge adb/sh quoting) and run it.</summary>
        private void RunDeviceScript(string script, string name)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r", "")));
            var path = $"/data/local/tmp/{name}.sh";
            sshClient.ExecuteBounded($"{adb} shell 'echo {b64} | base64 -d > {path} && sh {path}'");
        }
    }
}
