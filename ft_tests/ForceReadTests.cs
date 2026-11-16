using ft;
using ft.Commands;
using ft.Listeners;
using ft.Socks;
using ft.Streams;
using System.Diagnostics;
using System.Text;

namespace ft_tests
{
    /// <summary>
    /// A reader peeks at the next byte of the counterpart's file, and takes a zero byte for "not written yet". A
    /// network share can show a file's new size before the bytes written there, so a peek can meet zeros where a
    /// command is about to be. After ForceRead, the next peek has to read the file again, not get the zeros the
    /// stream's read buffer still holds. In-process tests over local temp files, with the test as the counterpart.
    /// </summary>
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class ForceReadTests
    {
        const int HeaderBytes = 10;   // the session id (8 bytes) and the two purge flags

        [TestMethod]
        [Timeout(15000)]
        public void ForceRead_AfterAPeekMetAZero_LetsTheNextPeekSeeTheByteWrittenThere()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[HeaderBytes + 64]);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream, Encoding.ASCII);
                stream.Seek(HeaderBytes, SeekOrigin.Begin);
                Assert.AreEqual(0, reader.PeekChar(), "The peek before the write");

                WriteAt(path, HeaderBytes, [ConnectResult.COMMAND_ID]);
                stream.ForceRead(10000, false);

                Assert.AreEqual(ConnectResult.COMMAND_ID, reader.PeekChar(), "The peek after the write and ForceRead");
                Assert.AreEqual(HeaderBytes, stream.Position, "The position after the peeks");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // A session's first command goes 10 bytes into the file. There the session id check, once a second, reads
        // through the same 4 KB buffer as the zeros, and leaves them in it, so only ForceRead can drop them before
        // the tunnel times out.
        [TestMethod]
        [Timeout(60000)]
        public void ReceivePump_WaitingAtZerosWhereTheFirstCommandGoes_ReadsTheCommandOnceItIsWritten()
        {
            const int connectionId = 7;
            var counterpartFile = Path.GetTempFileName();
            var ownFile = Path.GetTempFileName();
            NewSession(counterpartFile);

            var end = new ReusableFile(counterpartFile, ownFile, 10 * 1024 * 1024, 10000, false, false);
            var sessions = 0;
            end.SessionChanged += (s, e) => Interlocked.Increment(ref sessions);
            end.RegisterConnectResultWaiter(connectionId);
            new Thread(end.ReceivePump) { IsBackground = true }.Start();
            WaitUntil(() => Volatile.Read(ref sessions) == 1, "the ReceivePump reading the counterpart's session");

            // The file's size grows past the first command before the command can be read there.
            using (var file = new FileStream(counterpartFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                file.SetLength(HeaderBytes + 64);
            }

            Thread.Sleep(2500);   // the ReceivePump peeks at the zeros, and checks the session id twice meanwhile

            WriteAt(counterpartFile, HeaderBytes, Serialise(new ConnectResult(connectionId, (byte)ConnectStatus.Success)));
            var waited = Stopwatch.StartNew();
            var status = end.AwaitConnectResult(connectionId, 3000);

            Assert.AreEqual((byte)ConnectStatus.Success, status, $"The ReceivePump hadn't read the command {waited.ElapsedMilliseconds:N0} ms after it was written. The tunnel timeout is 10,000 ms.");
        }

        // The counterpart starting a session in its file: a new session id, both flags clear, no commands yet.
        static void NewSession(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.SetLength(HeaderBytes);
            file.Write(BitConverter.GetBytes(Random.Shared.NextInt64(1, long.MaxValue)));
            file.Write(new byte[HeaderBytes - sizeof(long)]);
        }

        static void WriteAt(string path, long position, byte[] bytes)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.Seek(position, SeekOrigin.Begin);
            file.Write(bytes);
        }

        static byte[] Serialise(Command command)
        {
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(new HashingStream(bytes, false, 10000)))
            {
                command.Serialise(writer);
            }

            return bytes.ToArray();
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
    }
}
