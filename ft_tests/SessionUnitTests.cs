using ft.Listeners;

namespace ft_tests
{
    /// <summary>
    /// How each end of a file tunnel recognises the other's session, and what it keeps across an outage:
    /// in-process tests over local temp files, so no lab is needed.
    /// </summary>
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class SessionUnitTests
    {
        const int HeaderBytes = 10;   // the session id (8 bytes) and the two purge flags

        // The writer creates its file and sizes it to the header before it writes its session id there. A
        // reader that opened the file in between took the zeros for the id; a second later it took the real
        // id for a counterpart that had restarted, restarted its own pump, and dropped every listener the
        // counterpart had asked it to host. It must wait for the id instead.
        [TestMethod]
        [Timeout(30000)]
        public void Reader_OpeningBeforeTheSessionIdIsWritten_SeesOneSession()
        {
            var readFilename = Path.GetTempFileName();
            var writeFilename = Path.GetTempFileName();
            File.WriteAllBytes(readFilename, new byte[HeaderBytes]);   // the writer's file, before the id

            var reader = new ReusableFile(readFilename, writeFilename, 10 * 1024 * 1024, 10000, false, false);
            var sessions = 0;
            reader.SessionChanged += (s, e) => Interlocked.Increment(ref sessions);
            new Thread(reader.ReceivePump) { IsBackground = true }.Start();

            Thread.Sleep(1000);   // the reader looks for the file every 200 ms
            using (var file = new FileStream(readFilename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                file.Write(BitConverter.GetBytes(Random.Shared.NextInt64(1, long.MaxValue)));
            }
            Thread.Sleep(3000);   // while idle, it re-reads the id once a second

            Assert.AreEqual(1, sessions, "The reader saw the writer's one session as more than one");
        }
    }
}
