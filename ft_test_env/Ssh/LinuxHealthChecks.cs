using ft_test_env.Config;
using ft_test_env.Steps;
using Renci.SshNet;

namespace ft_test_env.Ssh
{
    /// <summary>Verifies a Debian node over SSH: reachability, services, exports, mounts.</summary>
    public class LinuxHealthChecks
    {
        private readonly EnvConfig config;
        private readonly string mountsScriptPath;

        public LinuxHealthChecks(EnvConfig config)
        {
            this.config = config;
            // Copied next to the executable via the csproj (Cloud\mounts.sh).
            mountsScriptPath = Path.Combine(AppContext.BaseDirectory, "Cloud", "mounts.sh");
        }

        /// <summary>Blocks until SSH accepts a login or the configured timeout elapses.</summary>
        public StepOutcome WaitForSsh(NodeConfig node)
        {
            var deadline = DateTime.UtcNow.AddSeconds(config.Linux.SshReadyTimeoutSeconds);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = Connect(node, TimeSpan.FromSeconds(5));
                    return StepOutcome.Ok($"logged in to {node.Ip}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(3000);
                }
            }
            return StepOutcome.Fail($"no SSH within {config.Linux.SshReadyTimeoutSeconds}s ({last?.Message})");
        }

        /// <summary>
        /// Polls until the provisioning script's completion sentinel (/run/ft-setup-complete) exists.
        /// This is more reliable than waiting on services, which the package install auto-starts
        /// before the script finishes configuring shares, FTP and the proxy.
        /// </summary>
        public StepOutcome WaitForProvisioned(NodeConfig node)
        {
            var deadline = DateTime.UtcNow.AddSeconds(config.Linux.ServicesReadyTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = Connect(node, TimeSpan.FromSeconds(5));
                    var ready = client.RunCommand("test -f /run/ft-setup-complete && echo READY").Result.Trim();
                    if (ready.EndsWith("READY", StringComparison.Ordinal))
                    {
                        return StepOutcome.Ok("provisioning complete");
                    }
                }
                catch
                {
                    // node may still be booting / cloud-init mid-run
                }
                Thread.Sleep(5000);
            }
            return StepOutcome.Fail($"provisioning did not complete within {config.Linux.ServicesReadyTimeoutSeconds}s");
        }

        /// <summary>Runs the full set of checks for a node through the given StepRunner.</summary>
        public void CheckNode(StepRunner step, NodeConfig node)
        {
            SshClient? client = null;
            step.Run($"{node.Name}: SSH reachable ({node.Ip})", () =>
            {
                client = Connect(node, TimeSpan.FromSeconds(8));
                return StepOutcome.Ok();
            });

            if (client is null) return;   // nothing else will work without SSH

            using (client)
            {
                foreach (var service in config.Linux.Services)
                {
                    step.Run($"{node.Name}: service '{service}' active", () => ServiceActive(client, service));
                }

                if (node.IsServer)
                {
                    step.Run($"{node.Name}: NFS export '/mnt/tmpfs'", () => ExportPresent(client, "/mnt/tmpfs"));
                    step.Run($"{node.Name}: tmpfs mounted at '/mnt/tmpfs'", () => Mounted(client, "/mnt/tmpfs"));
                }

                foreach (var mount in config.Linux.ExpectedMounts)
                {
                    step.Run($"{node.Name}: mount '{mount}'", () => Mounted(client, mount));
                }
            }
        }

        /// <summary>
        /// Mounts any shares that aren't already mounted on a node, by streaming the local mounts.sh
        /// over SSH and running it (it is idempotent and non-fatal). Works on any running node —
        /// including ones provisioned before mounts.sh existed — and applies the latest mount logic
        /// without a reboot. The subsequent health checks report the resulting per-mount state.
        /// </summary>
        public void EnsureMounts(StepRunner step, NodeConfig node)
        {
            step.Run($"{node.Name}: mount shares (if not already mounted)", () =>
            {
                if (!File.Exists(mountsScriptPath))
                    return StepOutcome.Fail($"mounts.sh not found at {mountsScriptPath}");

                using var client = Connect(node, TimeSpan.FromSeconds(8));
                var scriptB64 = Convert.ToBase64String(File.ReadAllBytes(mountsScriptPath));
                var command = client.RunCommand($"echo {scriptB64} | base64 -d | sudo bash 2>&1");

                return command.ExitStatus == 0
                    ? StepOutcome.Ok()
                    : StepOutcome.Fail($"exit {command.ExitStatus}: {OneLine(command.Result)}");
            });
        }

        private static string OneLine(string text)
        {
            var collapsed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return collapsed.Length > 160 ? collapsed[..160] + "…" : collapsed;
        }

        private SshClient Connect(NodeConfig node, TimeSpan timeout)
        {
            var auth = new PasswordAuthenticationMethod(config.Linux.Username, config.Linux.Password);
            var info = new ConnectionInfo(node.Ip, config.Linux.SshPort, config.Linux.Username, auth)
            {
                Timeout = timeout
            };
            var client = new SshClient(info);
            client.Connect();
            return client;
        }

        private static StepOutcome ServiceActive(SshClient client, string service)
        {
            var state = client.RunCommand($"systemctl is-active {service}").Result.Trim();
            return state == "active" ? StepOutcome.Ok(state) : StepOutcome.Fail($"state is '{state}'");
        }

        private static StepOutcome ExportPresent(SshClient client, string path)
        {
            var exports = client.RunCommand("sudo exportfs -s 2>/dev/null || sudo exportfs").Result;
            return exports.Contains(path, StringComparison.Ordinal)
                ? StepOutcome.Ok()
                : StepOutcome.Fail("not exported");
        }

        private static StepOutcome Mounted(SshClient client, string path)
        {
            var result = client.RunCommand($"mountpoint -q '{path}' && echo yes || echo no").Result.Trim();
            return result.EndsWith("yes", StringComparison.Ordinal)
                ? StepOutcome.Ok()
                : StepOutcome.Fail("not mounted");
        }
    }
}
