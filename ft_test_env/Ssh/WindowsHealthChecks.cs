using System.Diagnostics;
using System.Net.Sockets;
using ft_test_env.Config;
using ft_test_env.Steps;
using Renci.SshNet;

namespace ft_test_env.Ssh
{
    /// <summary>
    /// Verifies the always-on Windows machines are ready: TCP ports, SMB shares (remote),
    /// and 'net share' / path existence (local dev box). Provisioning Windows is out of scope.
    /// </summary>
    public class WindowsHealthChecks
    {
        private readonly EnvConfig config;

        public WindowsHealthChecks(EnvConfig config)
        {
            this.config = config;
        }

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

        /// <summary>
        /// Confirms a process is bound to the given UDP port on the host. A UDP service such as
        /// runremote cannot be verified with a TCP connect (there is no handshake, and the closed
        /// TCP port reads as "unreachable" whether or not the listener is up), so we log in over
        /// SSH and inspect the UDP endpoints. 'netstat -ano -p udp' restricts the listing to UDP
        /// and runs identically under cmd.exe and PowerShell, so the result is shell-independent.
        /// </summary>
        private StepOutcome UdpBound(WindowsHostConfig host, int port)
        {
            var cred = config.ResolveCredential(host.CredentialKey);
            if (cred is null)
                return StepOutcome.Fail($"no credentials (key '{host.CredentialKey}') for the SSH-based UDP check");

            try
            {
                var auth = new PasswordAuthenticationMethod(cred.Username, cred.Password);
                var info = new ConnectionInfo(host.Host, 22, cred.Username, auth)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };
                using var client = new SshClient(info);
                client.Connect();

                var command = client.RunCommand($"netstat -ano -p udp | findstr :{port}");
                var bound = command.Result.Contains($":{port}", StringComparison.Ordinal);

                return bound
                    ? StepOutcome.Ok($"UDP {port} bound")
                    : StepOutcome.Fail($"nothing bound to UDP {port}");
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
                // Relies on the current Windows session already having access (as the harness does).
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
