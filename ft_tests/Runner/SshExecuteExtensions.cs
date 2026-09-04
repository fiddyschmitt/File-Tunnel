using Renci.SshNet;
using System;

namespace ft_tests.Runner
{
    /// <summary>
    /// Every SSH command in the test runners funnels through here with a bounded CommandTimeout. SSH.NET's
    /// SshCommand.Execute() defaults to an INFINITE CommandTimeout, so a wedged remote - or, more commonly, a
    /// channel that never signals close - blocks forever and hangs the whole run (ClassInitialize has no
    /// timeout of its own, so one stuck setup command wedges the entire class). Bounding every command turns
    /// that into a fast, local failure: the affected setup step / test fails or skips, and the suite proceeds.
    /// </summary>
    public static class SshExecuteExtensions
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Run a command, returning its stdout; on timeout return "" instead of blocking forever.</summary>
        public static string ExecuteBounded(this SshClient client, string commandText, int? timeoutSeconds = null)
        {
            using var cmd = client.CreateCommand(commandText);
            cmd.CommandTimeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : DefaultTimeout;
            try
            {
                return cmd.Execute();
            }
            catch (Renci.SshNet.Common.SshOperationTimeoutException)
            {
                return ""; // the command (or its channel close) hung - treat as no output rather than wedge the suite
            }
        }
    }
}
