using ft.Listeners;
using ft_tests.Utilities;
using System.Diagnostics;

namespace ft_tests
{
    /// <summary>
    /// What a ReusableFile pump leaves open when it starts over after an error: in-process tests over local temp
    /// files, with the test as the counterpart. A pump that starts over opens its file afresh, and closes what it
    /// had open, rather than leave that to the garbage collector. The handles on the two ready flags are used by
    /// both pumps, so the pump that opened one replaces it while the other pump may be using it.
    /// </summary>
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class PumpRestartTests
    {
        const int HeaderBytes = 10;   // the session id (8 bytes) and the two purge flags
        const int ReadyFlag = 8;
        const int CompleteFlag = 9;

        // A counterpart that starts over makes this end's ReceivePump start over too, and open the counterpart's file
        // again: for the commands, and for each of the counterpart's two flags.
        [TestMethod]
        [Timeout(60000)]
        public void ReceivePump_StartingOver_LeavesOnlyItsNewHandlesOpen()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive(CountsOnWindowsOnly);
                return;
            }

            var counterpartFile = NewFile();
            var ownFile = Path.GetTempFileName();
            NewSession(counterpartFile);

            var end = new ReusableFile(counterpartFile, ownFile, 10 * 1024 * 1024, 10000, false, false);
            var sessions = 0;
            end.SessionChanged += (s, e) => Interlocked.Increment(ref sessions);
            new Thread(end.ReceivePump) { IsBackground = true }.Start();

            var counts = new List<int>();
            for (var restarts = 0; restarts <= 4; restarts++)
            {
                if (restarts > 0)
                {
                    NewSession(counterpartFile);
                }

                WaitUntil(() => Volatile.Read(ref sessions) == restarts + 1, "the ReceivePump reading the counterpart's session");
                Thread.Sleep(500);   // the flag readers are opened just after the session id is read
                counts.Add(HandlesOn(counterpartFile));
            }

            Assert.IsTrue(counts.All(c => c == 3), $"Handles open on the counterpart's file at the start and after each of 4 restarts: {string.Join(", ", counts)}");
        }

        // A write that fails makes the SendPump start over: it creates its file afresh, with a new session id, and
        // opens it for its commands and for each of its two flags.
        [TestMethod]
        [Timeout(60000)]
        public void SendPump_StartingOver_LeavesOnlyItsNewHandlesOpen()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive(CountsOnWindowsOnly);
                return;
            }

            var counterpartFile = Path.GetTempFileName();
            var ownFile = Path.GetTempFileName();

            var end = new ReusableFile(counterpartFile, ownFile, 10 * 1024 * 1024, 1000, false, false);
            new Thread(end.SendPump) { IsBackground = true }.Start();
            WaitUntil(() => SessionId(ownFile) != 0 && HandlesOn(ownFile) == 3, "the SendPump opening its file");
            var firstSession = SessionId(ownFile);

            // Another handle locks the whole file, so the pump's next write fails until the tunnel timeout, and it
            // starts over, and again while the lock lasts.
            using (var locker = new FileStream(ownFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                locker.Lock(0, int.MaxValue);
                end.SendConnectResult(1, 0);
                Thread.Sleep(3500);
                locker.Unlock(0, int.MaxValue);
            }

            WaitUntil(() => SessionId(ownFile) is var id && id != 0 && id != firstSession, "the SendPump starting over, with a new session");
            Thread.Sleep(500);   // its flag writers are opened just after the session id is written

            Assert.AreEqual(3, HandlesOn(ownFile), "Handles open on this end's file after the SendPump started over");
        }

        // The SendPump waits on the counterpart's ready flag during its purge, through the reader the ReceivePump
        // opened. When the ReceivePump starts over meanwhile, it opens a new reader and closes the old one. The
        // SendPump has to go on waiting, through the new one, and finish its purge, not fail on the closed reader and
        // start over with a new session id, which would make the counterpart close every connection.
        [TestMethod]
        [Timeout(90000)]
        public void SendPump_WaitingForTheCounterpartsReadyFlag_OutlastsTheReceivePumpStartingOver()
        {
            const int purgeSize = 1024;
            var counterpartFile = NewFile();
            var ownFile = Path.GetTempFileName();
            NewSession(counterpartFile);

            var end = new ReusableFile(counterpartFile, ownFile, purgeSize, 10000, false, false);
            var sessions = 0;
            end.SessionChanged += (s, e) => Interlocked.Increment(ref sessions);
            new Thread(end.ReceivePump) { IsBackground = true }.Start();
            new Thread(end.SendPump) { IsBackground = true }.Start();
            WaitUntil(() => Volatile.Read(ref sessions) == 1 && SessionId(ownFile) != 0, "both pumps under way");
            var ownSession = SessionId(ownFile);

            // More commands than fit under the purge size: the SendPump asks the counterpart to get ready for a purge,
            // and waits for its ready flag. The commands it can't send meanwhile wait on another thread.
            new Thread(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    end.SendConnectResult(i, 0);
                }
            }) { IsBackground = true }.Start();
            WaitUntilTheFileStopsGrowing(ownFile, "the SendPump waiting for the counterpart to get ready for its purge");

            // The counterpart starts over, so this end's ReceivePump does too, and replaces the flag reader.
            NewSession(counterpartFile);
            WaitUntil(() => Volatile.Read(ref sessions) == 2, "the ReceivePump reading the counterpart's new session");
            Thread.Sleep(500);

            // The counterpart gets ready: the SendPump purges its file and sets its complete flag, then clears it once
            // the counterpart has cleared its ready flag.
            SetFlag(counterpartFile, ReadyFlag, 1);
            WaitUntil(() => FlagOf(ownFile, CompleteFlag) == 1, "the SendPump purging its file");
            SetFlag(counterpartFile, ReadyFlag, 0);
            WaitUntil(() => FlagOf(ownFile, CompleteFlag) == 0, "the SendPump finishing its purge");

            Assert.AreEqual(ownSession, SessionId(ownFile), "The SendPump started over, with a new session id");
            if (OperatingSystem.IsWindows())
            {
                Assert.AreEqual(3, HandlesOn(counterpartFile), "Handles open on the counterpart's file after the ReceivePump started over");
            }
        }

        const string CountsOnWindowsOnly = "Counts the handles open on a file, which these tests do on Windows only";

        static string NewFile()
        {
            var path = Path.GetTempFileName();
            File.WriteAllBytes(path, new byte[HeaderBytes]);
            return path;
        }

        // The counterpart starting a session in its file: a new session id, both flags clear, no commands yet.
        static void NewSession(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.SetLength(HeaderBytes);
            file.Write(BitConverter.GetBytes(Random.Shared.NextInt64(1, long.MaxValue)));
            file.Write(new byte[HeaderBytes - sizeof(long)]);
        }

        // 0 while the file has no session id, is being created afresh, or is locked.
        static long SessionId(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> id = stackalloc byte[sizeof(long)];
                return file.ReadAtLeast(id, id.Length, throwOnEndOfStream: false) == id.Length ? BitConverter.ToInt64(id) : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        static int FlagOf(string path, int position)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(position, SeekOrigin.Begin);
            return file.ReadByte();
        }

        static void SetFlag(string path, int position, byte value)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.Seek(position, SeekOrigin.Begin);
            file.WriteByte(value);
        }

        // Counted on another thread, so that a handle the count can't ask about doesn't hang the test.
        static int HandlesOn(string path)
        {
            var count = Task.Run(() => OpenHandles.On(path));
            if (!count.Wait(TimeSpan.FromSeconds(20)))
            {
                Assert.Inconclusive("Counting the handles open on the file took too long");
            }

            return count.Result;
        }

        static void WaitUntil(Func<bool> condition, string what, int timeoutMilliseconds = 20000)
        {
            var waited = Stopwatch.StartNew();
            while (!condition())
            {
                if (waited.ElapsedMilliseconds > timeoutMilliseconds)
                {
                    Assert.Fail($"Timed out waiting for {what}");
                }

                Thread.Sleep(50);
            }
        }

        static void WaitUntilTheFileStopsGrowing(string path, string what)
        {
            var length = -1L;
            var unchanged = Stopwatch.StartNew();
            WaitUntil(() =>
            {
                var now = new FileInfo(path).Length;
                if (now != length)
                {
                    length = now;
                    unchanged.Restart();
                }

                return length > HeaderBytes && unchanged.ElapsedMilliseconds > 1000;
            }, what);
        }
    }
}
