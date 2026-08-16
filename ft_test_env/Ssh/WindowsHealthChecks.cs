using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ft_test_env.Config;
using ft_test_env.Steps;
using Renci.SshNet;

namespace ft_test_env.Ssh
{
    /// <summary>
    /// Drives the Windows gold + dedicated clones over SSH (the gold has OpenSSH with a PowerShell default
    /// shell, inherited from rclone-win-gold). All clones share the gold's credentials. A fresh clone boots
    /// at the gold's SourceIp, then we reconfigure it (rename + new static IP) and reboot — launched detached
    /// via Win32_Process.Create so it survives the IP change and the SSH disconnect. Also health-checks the
    /// running clones for ft-readiness, and the dev box (.31) for its local shares.
    /// (Copied and adapted from rclone_test_env/Ssh/WindowsHealthChecks.cs — the proven technique.)
    /// </summary>
    public class WindowsHealthChecks
    {
        private readonly EnvConfig config;

        public WindowsHealthChecks(EnvConfig config)
        {
            this.config = config;
        }

        private WindowsGoldConfig Gold => config.WindowsGold;

        private SshClient Connect(string ip, TimeSpan timeout)
        {
            var auth = new PasswordAuthenticationMethod(Gold.Username, Gold.Password);
            var info = new ConnectionInfo(ip, Gold.SshPort, Gold.Username, auth) { Timeout = timeout };
            var client = new SshClient(info);
            client.Connect();
            return client;
        }

        // ---- clone lifecycle (gold shutdown -> reconfigure -> confirm) ----

        /// <summary>Requests a clean Windows shutdown/reboot over SSH (more reliable than the ACPI button).</summary>
        public bool RequestShutdown(string ip, bool reboot = false)
        {
            try
            {
                using var client = Connect(ip, TimeSpan.FromSeconds(8));
                client.RunCommand(reboot ? "shutdown /r /t 0 /f" : "shutdown /s /t 0 /f");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Blocks until SSH accepts a login at the given IP, or the timeout elapses.</summary>
        public StepOutcome WaitForSsh(string ip, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = Connect(ip, TimeSpan.FromSeconds(5));
                    return StepOutcome.Ok($"SSH up at {ip}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(3000);
                }
            }
            return StepOutcome.Fail($"no SSH at {ip} within {timeoutSeconds}s ({last?.Message})");
        }

        /// <summary>
        /// Blocks until SSH at the given IP STOPS accepting logins (the node has begun going down), or the
        /// timeout elapses. Used right after requesting a reboot: <c>shutdown /r</c> keeps sshd up for a few
        /// seconds, so a bare WaitForSsh would reconnect to the still-running pre-reboot node and mistake it
        /// for the rebooted one. Waiting for it to go down first closes that race.
        /// </summary>
        public StepOutcome WaitForSshDown(string ip, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = Connect(ip, TimeSpan.FromSeconds(4));
                }
                catch
                {
                    return StepOutcome.Ok($"{ip} went down");
                }
                Thread.Sleep(2000);
            }
            return StepOutcome.Fail($"{ip} still reachable after {timeoutSeconds}s (reboot did not take?)");
        }

        /// <summary>
        /// Launches the clone's reconfiguration (rename + new static IP + reboot) detached on the clone,
        /// connecting at the gold's SourceIp where the fresh clone is currently sitting. Returns once the
        /// detached job is launched; the clone then reboots onto its target IP.
        /// </summary>
        public StepOutcome LaunchReconfigure(WindowsNodeConfig node)
        {
            var script = BuildReconfigScript(node);
            var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var cmdLine = $"powershell.exe -ExecutionPolicy Bypass -EncodedCommand {b64}";

            using var client = Connect(Gold.SourceIp, TimeSpan.FromSeconds(8));
            // Win32_Process.Create detaches from the SSH session, so the IP change + reboot complete
            // even though our connection (on SourceIp) drops the moment the IP moves.
            client.RunCommand($"Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{ CommandLine = '{cmdLine}' }} | Out-Null");
            return StepOutcome.Ok($"reconfig launched -> {node.Hostname} @ {node.Ip} (rebooting)");
        }

        private string BuildReconfigScript(WindowsNodeConfig node)
        {
            var src = Gold.SourceIp;
            var net = config.Network;
            var mask = PrefixToMask(net.PrefixLength);
            // netsh 'set address static' replaces the IP + gateway atomically — unlike Remove + New-
            // NetIPAddress, which can strand the box (the old default route makes the gateway add fail).
            // Logged to C:\Temp\reconfig.log on the clone so a failure is diagnosable after the reboot.
            return string.Join("\r\n", new[]
            {
                "$log='C:\\Temp\\reconfig.log'",
                "New-Item -ItemType Directory -Force -Path 'C:\\Temp' | Out-Null",
                "function L($m){ Add-Content -Path $log -Value $m }",
                "$alias=(Get-NetIPAddress -IPAddress " + src + " -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceAlias",
                "if (-not $alias) { $alias=(Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -First 1).Name }",
                "L ('alias=' + $alias)",
                "try { Rename-Computer -NewName '" + node.Hostname + "' -Force -ErrorAction Stop; L 'rename OK' } catch { L ('rename ERR: ' + $_.Exception.Message) }",
                "$r = netsh interface ip set address name=\"$alias\" static " + node.Ip + " " + mask + " " + net.Gateway,
                "L ('netsh-ip rc=' + $LASTEXITCODE + ' ' + ($r -join ' '))",
                "$r2 = netsh interface ip set dns name=\"$alias\" static " + net.Dns + " validate=no",
                "L ('netsh-dns rc=' + $LASTEXITCODE + ' ' + ($r2 -join ' '))",
                "L ('ips=' + ((Get-NetIPAddress -AddressFamily IPv4).IPAddress -join ','))",
                "L 'rebooting'",
                "Start-Sleep -Seconds 3",
                "Restart-Computer -Force",
            });
        }

        private static string PrefixToMask(int prefix)
        {
            var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
        }

        /// <summary>Confirms a reconfigured clone: reachable at its target IP, with the right hostname.</summary>
        public StepOutcome ConfirmClone(WindowsNodeConfig node)
        {
            try
            {
                using var client = Connect(node.Ip, TimeSpan.FromSeconds(8));
                var name = client.RunCommand("$env:COMPUTERNAME").Result.Trim();
                var hasIp = client.RunCommand("if (Get-NetIPAddress -IPAddress " + node.Ip + " -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }").Result.Trim();
                var nameOk = string.Equals(name, node.Hostname, StringComparison.OrdinalIgnoreCase);
                return nameOk && hasIp == "yes"
                    ? StepOutcome.Ok($"{name} @ {node.Ip}")
                    : StepOutcome.Fail($"hostname='{name}' (want '{node.Hostname}'), ip {node.Ip}={hasIp}");
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Polls the target IP until the clone is fully reconfigured: reachable AND reporting its new
        /// hostname. netsh sets the new IP *before* the reboot, so the new IP is briefly reachable with
        /// the OLD hostname; this rides through that pre-reboot window and the reboot-down window and
        /// only succeeds once the rename has taken effect.
        /// </summary>
        public StepOutcome ConfirmReconfigured(WindowsNodeConfig node)
        {
            var deadline = DateTime.UtcNow.AddSeconds(Gold.ReconfigTimeoutSeconds);
            var last = "no connection yet";
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = Connect(node.Ip, TimeSpan.FromSeconds(5));
                    var name = client.RunCommand("$env:COMPUTERNAME").Result.Trim();
                    if (string.Equals(name, node.Hostname, StringComparison.OrdinalIgnoreCase))
                        return StepOutcome.Ok($"{name} @ {node.Ip}");
                    last = $"hostname still '{name}' (pre-reboot)";
                }
                catch (Exception ex)
                {
                    last = ex.Message;
                }
                Thread.Sleep(4000);
            }
            return StepOutcome.Fail($"not reconfigured within {Gold.ReconfigTimeoutSeconds}s ({last})");
        }

        // ---- ft-readiness of a running clone ----

        /// <summary>Health-checks a running clone for ft-readiness (menu "Check Windows nodes" + post-bring-up
        /// / post-reboot). Everything below is baked into the gold, so all nodes are checked uniformly.</summary>
        public void CheckNode(StepRunner step, WindowsNodeConfig node)
        {
            step.Run($"{node.CloneName}: identity ({node.Hostname} @ {node.Ip})", () => ConfirmClone(node));

            SshClient? client = null;
            step.Run($"{node.CloneName}: SSH login", () =>
            {
                try { client = Connect(node.Ip, TimeSpan.FromSeconds(8)); return StepOutcome.Ok(); }
                catch (Exception ex) { return StepOutcome.Fail(ex.Message); }
            });
            if (client is null) return;

            using (client)
            {
                string Out(string cmd) => client.RunCommand(cmd).Result.Trim();

                step.Run($"{node.CloneName}: runremote UDP 8888 listening", () =>
                {
                    var v = Out("netstat -ano -p udp | findstr :8888");
                    return v.Contains(":8888", StringComparison.Ordinal)
                        ? StepOutcome.Ok()
                        : StepOutcome.Fail("runremote not bound to UDP 8888 (autologon/autostart in session 1?)");
                });
                step.Run($"{node.CloneName}: C:\\Temp\\ft writable", () =>
                {
                    var v = Out("try { New-Item -ItemType Directory -Force C:\\Temp\\ft | Out-Null; Set-Content C:\\Temp\\ft\\.probe 'x'; Remove-Item C:\\Temp\\ft\\.probe; 'ok' } catch { $_.Exception.Message }");
                    return v == "ok" ? StepOutcome.Ok() : StepOutcome.Fail(v);
                });
                step.Run($"{node.CloneName}: 'Shared' SMB share", () =>
                {
                    var v = Out("if (Get-SmbShare -Name Shared -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("Shared share missing");
                });
                step.Run($"{node.CloneName}: RDP 3389 open", () =>
                    TcpOpen(node.Ip, 3389, 3000) ? StepOutcome.Ok() : StepOutcome.Fail("3389 closed"));
                step.Run($"{node.CloneName}: Client-for-NFS (mount.exe)", () =>
                {
                    var v = Out("if (Get-Command mount.exe -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("mount.exe missing (Client for NFS)");
                });
                step.Run($"{node.CloneName}: \\\\vboxsvr\\c_drive (Guest Additions)", () =>
                {
                    var v = Out("if (Test-Path \\\\vboxsvr\\c_drive) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("\\\\vboxsvr\\c_drive not visible (Guest Additions + c_drive shared folder)");
                });
            }
        }

        /// <summary>Health-checks the hand-built SMB/RDP server VM (.84): SSH, runremote UDP 8888, the Shared
        /// share, RDP - and, critically, that its machine SID is DISTINCT from the client clones (a same-SID
        /// server can't serve SMB/RDP to the clones; Windows 24H2+ rejects it). Uses the gold's smith creds.</summary>
        public void CheckServer(StepRunner step, WindowsServerConfig server)
        {
            SshClient? client = null;
            step.Run($"{server.VmName}: SSH login ({server.Ip})", () =>
            {
                try { client = Connect(server.Ip, TimeSpan.FromSeconds(8)); return StepOutcome.Ok(); }
                catch (Exception ex) { return StepOutcome.Fail(ex.Message); }
            });
            if (client is null) return;

            using (client)
            {
                string Out(string cmd) => client.RunCommand(cmd).Result.Trim();

                step.Run($"{server.VmName}: runremote UDP 8888 listening", () =>
                {
                    var v = Out("netstat -ano -p udp | findstr :8888");
                    return v.Contains(":8888", StringComparison.Ordinal)
                        ? StepOutcome.Ok()
                        : StepOutcome.Fail("runremote not bound to UDP 8888 (autologon/autostart in session 1?)");
                });
                step.Run($"{server.VmName}: 'Shared' SMB share", () =>
                {
                    var v = Out("if (Get-SmbShare -Name Shared -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("Shared share missing");
                });
                step.Run($"{server.VmName}: RDP 3389 open", () =>
                    TcpOpen(server.Ip, 3389, 3000) ? StepOutcome.Ok() : StepOutcome.Fail("3389 closed"));

                // The whole reason this is a separate hand-built VM: its machine SID must DIFFER from the client
                // clones, or Windows 24H2+ rejects SMB/RDP auth between them. Compare against a reachable client.
                step.Run($"{server.VmName}: machine SID distinct from client clones", () =>
                {
                    var serverSid = MachineSid(Out("(Get-LocalUser -Name smith -ErrorAction SilentlyContinue).SID.Value"));
                    if (string.IsNullOrEmpty(serverSid)) return StepOutcome.Fail("could not read the server's machine SID");
                    foreach (var node in config.WindowsNodes)
                    {
                        try
                        {
                            using var c = Connect(node.Ip, TimeSpan.FromSeconds(6));
                            var clientSid = MachineSid(c.RunCommand("(Get-LocalUser -Name smith -ErrorAction SilentlyContinue).SID.Value").Result.Trim());
                            if (string.IsNullOrEmpty(clientSid)) continue;
                            return serverSid == clientSid
                                ? StepOutcome.Fail($"SERVER SHARES THE CLONE SID ({serverSid}) — it must be a fresh install, NOT a clone")
                                : StepOutcome.Ok($"distinct (server {serverSid} != client {clientSid})");
                        }
                        catch { /* client down; try the next */ }
                    }
                    return StepOutcome.Ok($"server SID {serverSid} (no client reachable to compare against)");
                });
            }
        }

        /// <summary>The machine portion of a SID (drops the trailing account RID): S-1-5-21-a-b-c-1001 -> S-1-5-21-a-b-c.</summary>
        private static string MachineSid(string sid)
        {
            if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-5-21", StringComparison.Ordinal)) return "";
            var i = sid.LastIndexOf('-');
            return i > 0 ? sid[..i] : sid;
        }

        /// <summary>The gold-image readiness battery (run it before snapshotting / after any gold change).</summary>
        public void CheckGold(StepRunner step)
        {
            SshClient? client = null;
            step.Run($"{Gold.GoldVmName}: SSH login as '{Gold.Username}' ({Gold.SourceIp})", () =>
            {
                try { client = Connect(Gold.SourceIp, TimeSpan.FromSeconds(8)); return StepOutcome.Ok(); }
                catch (Exception ex) { return StepOutcome.Fail(ex.Message); }
            });
            if (client is null) return;

            using (client)
            {
                string Out(string cmd) => client.RunCommand(cmd).Result.Trim();

                step.Run("default shell is PowerShell", () =>
                {
                    var v = Out("$PSVersionTable.PSVersion.ToString()");
                    return v.Length > 0 && char.IsDigit(v[0]) ? StepOutcome.Ok(v) : StepOutcome.Fail("not PowerShell");
                });
                step.Run("DefaultShell = powershell.exe", () =>
                {
                    var v = Out("(Get-ItemProperty 'HKLM:\\SOFTWARE\\OpenSSH' -Name DefaultShell -ErrorAction SilentlyContinue).DefaultShell");
                    return v.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase) ? StepOutcome.Ok() : StepOutcome.Fail(v);
                });
                step.Run("sshd Running + Automatic", () =>
                {
                    var v = Out("(Get-Service sshd).Status.ToString() + '/' + (Get-Service sshd).StartType.ToString()");
                    return v.Contains("Running") && v.Contains("Automatic") ? StepOutcome.Ok(v) : StepOutcome.Fail(v);
                });
                step.Run($"static IP {Gold.SourceIp} (Manual)", () =>
                {
                    var v = Out("$x = Get-NetIPAddress -IPAddress " + Gold.SourceIp + " -AddressFamily IPv4 -ErrorAction SilentlyContinue; if ($x) { $x.PrefixOrigin } else { 'MISSING' }");
                    return v.Contains("Manual", StringComparison.OrdinalIgnoreCase) ? StepOutcome.Ok() : StepOutcome.Fail(v);
                });
                step.Run("autologon (AutoAdminLogon = 1)", () =>
                {
                    var v = Out("$p = Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' -ErrorAction SilentlyContinue; $p.AutoAdminLogon");
                    return v.Trim() == "1" ? StepOutcome.Ok() : StepOutcome.Fail(v);
                });
                step.Run("RDP enabled (fDenyTSConnections = 0)", () =>
                {
                    var v = Out("(Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections");
                    return v.Trim() == "0" ? StepOutcome.Ok() : StepOutcome.Fail($"fDenyTSConnections={v}");
                });
                step.Run("firewall inbound allow: TCP 22/445/3389 + UDP 8888", () =>
                {
                    var v = Out("$want=@(@('TCP',22),@('TCP',445),@('TCP',3389),@('UDP',8888)); $missing=@(); foreach($w in $want){ $r=Get-NetFirewallPortFilter -Protocol $w[0] -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq $w[1] } | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' }; if(-not $r){ $missing += ($w[0]+'/'+$w[1]) } }; if($missing){ 'missing ' + ($missing -join ',') } else { 'ok' }");
                    return v == "ok" ? StepOutcome.Ok() : StepOutcome.Fail(v);
                });
                step.Run("Client for NFS (mount.exe)", () =>
                {
                    var v = Out("if (Get-Command mount.exe -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("mount.exe missing");
                });
                step.Run("Guest Additions present", () =>
                {
                    var v = Out("if ((Get-Service VBoxService -ErrorAction SilentlyContinue) -or (Test-Path 'C:\\Program Files\\Oracle\\VirtualBox Guest Additions')) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("Guest Additions not installed");
                });
                step.Run("runremote server staged (C:\\Temp\\runremote\\server.exe)", () =>
                {
                    var v = Out("if (Test-Path C:\\Temp\\runremote\\server.exe) { 'yes' } else { 'no' }");
                    return v == "yes" ? StepOutcome.Ok() : StepOutcome.Fail("server.exe not staged");
                });
            }
        }

        // ---- dev box (.31) host checks (unchanged from the original external-Windows checker) ----

        public void CheckHost(StepRunner step, WindowsHostConfig host)
        {
            foreach (var check in host.Checks)
            {
                var label = $"{host.Name} ({host.Host}): {Describe(check)}";
                step.Run(label, () => RunCheck(host, check));
            }
        }

        private static string Describe(WindowsCheck check) => check.Type switch
        {
            WindowsCheckType.TcpPort => $"TCP {check.Port}{Suffix(check)}",
            WindowsCheckType.UdpListener => $"UDP {check.Port} listener{Suffix(check)}",
            WindowsCheckType.SmbShare => $"SMB share {check.Target}",
            WindowsCheckType.NetShare => $"net share '{check.Target}'",
            WindowsCheckType.PathExists => $"path '{check.Target}'",
            _ => check.Type.ToString()
        };

        private static string Suffix(WindowsCheck check) =>
            string.IsNullOrWhiteSpace(check.Description) ? "" : $" ({check.Description})";

        private StepOutcome RunCheck(WindowsHostConfig host, WindowsCheck check) => check.Type switch
        {
            WindowsCheckType.TcpPort => TcpOpen(host.Host, check.Port, 3000)
                ? StepOutcome.Ok()
                : StepOutcome.Fail("port closed/unreachable"),

            WindowsCheckType.UdpListener => UdpBound(host, check.Port),

            WindowsCheckType.SmbShare => SmbShareListable(check.Target),

            WindowsCheckType.NetShare => NetShareExists(check.Target),

            WindowsCheckType.PathExists => check.Target != null && (Directory.Exists(check.Target) || File.Exists(check.Target))
                ? StepOutcome.Ok()
                : StepOutcome.Fail("not found"),

            _ => StepOutcome.Fail($"unknown check type {check.Type}")
        };

        private StepOutcome UdpBound(WindowsHostConfig host, int port)
        {
            var cred = config.ResolveCredential(host.CredentialKey);
            if (cred is null)
                return StepOutcome.Fail($"no credentials (key '{host.CredentialKey}') for the SSH-based UDP check");

            try
            {
                var auth = new PasswordAuthenticationMethod(cred.Username, cred.Password);
                var info = new ConnectionInfo(host.Host, 22, cred.Username, auth) { Timeout = TimeSpan.FromSeconds(8) };
                using var client = new SshClient(info);
                client.Connect();
                var command = client.RunCommand($"netstat -ano -p udp | findstr :{port}");
                var bound = command.Result.Contains($":{port}", StringComparison.Ordinal);
                return bound ? StepOutcome.Ok($"UDP {port} bound") : StepOutcome.Fail($"nothing bound to UDP {port}");
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }

        private static bool TcpOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                return connectTask.Wait(timeoutMs) && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static StepOutcome SmbShareListable(string? unc)
        {
            if (string.IsNullOrWhiteSpace(unc)) return StepOutcome.Fail("no share configured");
            try
            {
                _ = Directory.EnumerateFileSystemEntries(unc).Take(1).ToList();
                return StepOutcome.Ok();
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }

        private static StepOutcome NetShareExists(string? shareName)
        {
            if (string.IsNullOrWhiteSpace(shareName)) return StepOutcome.Fail("no share name configured");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("share");

                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var found = output
                    .Split('\n')
                    .Any(line => line.TrimStart().StartsWith(shareName + " ", StringComparison.OrdinalIgnoreCase)
                                 || line.Trim().Equals(shareName, StringComparison.OrdinalIgnoreCase));

                return found ? StepOutcome.Ok() : StepOutcome.Fail("share not listed");
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }
    }
}
