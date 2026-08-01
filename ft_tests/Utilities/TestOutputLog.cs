namespace ft_tests.Utilities
{
    /// <summary>
    /// Serialises appends to the shared ft output log.
    ///
    /// Four writers target the same file: <see cref="Runner.LocalWindowsProcessRunner"/>'s stdout AND
    /// stderr handlers - each raised on its own thread by <see cref="System.Diagnostics.Process"/> - plus
    /// the test-number and separator lines written by EndToEndTests.ConductTest on the test thread.
    ///
    /// Unsynchronised, that corrupted whole runs rather than just the log. File.AppendAllText opens with
    /// FileShare.Read, so a second concurrent append is denied and throws IOException; because two of the
    /// writers are Process event handlers, that exception is unhandled on an event thread, which
    /// terminates the process. Runs died with "Test host process crashed" at random points - after 16 SMB
    /// rows one time, 5 NFS rows the next - which read as lab flakiness rather than a harness bug.
    ///
    /// Logging is best-effort besides: writing a diagnostic line must never be able to end a run, so
    /// failures are swallowed instead of propagating back to an event-handler thread.
    /// </summary>
    public static class TestOutputLog
    {
        static readonly object writeLock = new();

        public static void Append(string path, string text)
        {
            lock (writeLock)
            {
                try { File.AppendAllText(path, text); }
                catch { }
            }
        }

        public static void AppendLine(string path, string line) => Append(path, line + Environment.NewLine);
    }
}
