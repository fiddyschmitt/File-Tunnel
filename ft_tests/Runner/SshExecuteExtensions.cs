using Renci.SshNet;
using System;
using System.Threading.Tasks;

namespace ft_tests.Runner
{
    /// <summary>
    /// Every SSH command in the test runners funnels through here with a HARD, wall-clock bound. SSH.NET's
    /// SshCommand.CommandTimeout bounds only the command's EXECUTION - it does NOT cover ChannelSession.Open(),
    /// the channel-open handshake that runs BEFORE the command. On a half-dead session (TCP still up, but the
    /// remote sshd never answers channel-open - exactly what a tiring Windows node does) Open() waits forever,
    /// and CommandTimeout can't fire because the command never started. That wedged an entire 5x suite run for
    /// 4.5h in RemoteWindowsProcessRunner.Stop(). So we run Execute() on a background task and cap the WALL-CLOCK
    /// wait here (covering open + execution); on timeout we dispose the command - which closes the channel/socket
    /// and unblocks the abandoned Execute thread - and return empty. A wedged op now costs seconds, not the run.
    /// </summary>
    public static class SshExecuteExtensions
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Run a command, returning its stdout; never blocks past the (hard) timeout - return "" instead.</summary>
        public static string ExecuteBounded(this SshClient client, string commandText, int? timeoutSeconds = null)
            => client.ExecuteHardBounded(commandText, timeoutSeconds).Output;

        /// <summary>
        /// As ExecuteBounded, but also returns the exit status and whether the command actually completed within
        /// the budget (false => it hung, typically in channel-open, and was abandoned).
        /// </summary>
        public static (string Output, int ExitStatus, bool Completed) ExecuteHardBounded(this SshClient client, string commandText, int? timeoutSeconds = null)
        {
            var budget = TimeSpan.FromSeconds(timeoutSeconds ?? (int)DefaultTimeout.TotalSeconds);
            var cmd = client.CreateCommand(commandText);
            cmd.CommandTimeout = budget; // bounds EXECUTION once the channel is open

            // Execute on a background task so we can cap the wall-clock wait - CommandTimeout can't bound
            // ChannelSession.Open(). Swallow every exception (timeout, channel error, dispose race): these are
            // best-effort harness commands and must never throw back and abort ClassInit / a test.
            var task = Task.Run(() =>
            {
                try { return cmd.Execute(); }
                catch { return ""; }
            });

            // +5s over the budget so the inner CommandTimeout (execution) gets to fire first when the channel
            // DID open; only a channel-open hang (which CommandTimeout can't catch) reaches this outer cap.
            if (task.Wait(budget + TimeSpan.FromSeconds(5)))
            {
                var status = cmd.ExitStatus ?? -1;
                var err = cmd.Error ?? "";
                try { cmd.Dispose(); } catch { }
                return (task.Result + err, status, true);
            }

            // Hung past the cap - almost certainly stuck in ChannelSession.Open() on a half-dead session.
            // Dispose to close the channel/socket and let the abandoned Execute thread unwind, then give up.
            try { cmd.Dispose(); } catch { }
            return ("", -1, false);
        }
    }
}
