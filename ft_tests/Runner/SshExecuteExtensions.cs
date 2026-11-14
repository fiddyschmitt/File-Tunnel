using Renci.SshNet;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
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
            SshCommand? cmd = null;
            var abandoned = 0;   // 1 once the wait below has given up on the command

            // Execute on a background task so we can cap the wall-clock wait - CommandTimeout can't bound
            // ChannelSession.Open(). Swallow every exception (timeout, channel error, dispose race): these are
            // best-effort harness commands and must never throw back and abort ClassInit / a test.
            var task = Task.Run(() =>
            {
                try
                {
                    Reconnect(client);

                    // Reconnecting can outlast the wait below (SSH.NET allows a connection 30 s). A command
                    // given up on meanwhile must not run after all: by then the test has moved on, and a
                    // late "kill ft" would stop the next row's ft.
                    if (Volatile.Read(ref abandoned) != 0)
                    {
                        return "";
                    }

                    var created = client.CreateCommand(commandText);
                    created.CommandTimeout = budget; // bounds EXECUTION once the channel is open

                    // Interlocked, here and where the wait gives up: each side writes its own word and then reads
                    // the other's, and at least one of them must see the other's write.
                    Interlocked.Exchange(ref cmd, created);
                    if (Volatile.Read(ref abandoned) != 0)
                    {
                        created.Dispose();
                        return "";
                    }

                    return created.Execute();
                }
                catch { return ""; }
            });

            // +5s over the budget so the inner CommandTimeout (execution) gets to fire first when the channel
            // DID open; only a channel-open hang (which CommandTimeout can't catch) reaches this outer cap.
            if (task.Wait(budget + TimeSpan.FromSeconds(5)))
            {
                var done = Volatile.Read(ref cmd);
                var status = done?.ExitStatus ?? -1;
                var err = done?.Error ?? "";
                try { done?.Dispose(); } catch { }
                return (task.Result + err, status, true);
            }

            // Hung past the cap - almost certainly stuck in ChannelSession.Open() on a half-dead session.
            // Dispose to close the channel/socket and let the abandoned Execute thread unwind, then give up.
            // A command that hasn't been created yet (the task is still reconnecting) is never run.
            Interlocked.Exchange(ref abandoned, 1);
            try { Volatile.Read(ref cmd)?.Dispose(); } catch { }
            return ("", -1, false);
        }

        // SSH.NET never reconnects by itself, so a connection that drops stays dropped. When a Windows node
        // crashed and restarted mid-run, every later command on it failed at once until the run ended: no ft
        // was stopped there again, and the ft left running broke the rows after it. So connect again first,
        // under a lock of the client's own, as threads share a client. The first command after a connection
        // dies can still fail, as SSH.NET only finds out by trying; the one after it reconnects.
        static readonly ConditionalWeakTable<SshClient, object> ReconnectLocks = new();

        static void Reconnect(SshClient client)
        {
            if (client.IsConnected)
            {
                return;
            }
            lock (ReconnectLocks.GetValue(client, _ => new object()))
            {
                if (!client.IsConnected)
                {
                    client.Connect();
                }
            }
        }
    }
}
