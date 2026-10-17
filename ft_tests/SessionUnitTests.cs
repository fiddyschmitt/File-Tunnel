using ft.Listeners;
using ft_tests.Utilities;
using System.Net;
using System.Net.Sockets;

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

        // One end can miss the other's writes for longer than the tunnel timeout while that other end still
        // hears it: a starved process, a slow share. The end that went quiet went offline and dropped the
        // proxy the other end had asked it to host (-R); the other end never saw the tunnel go down, so it
        // never asked again, and the proxy was gone for the rest of the session.
        [TestMethod]
        [Timeout(120000)]
        public void RemoteProxy_ComesBack_WhenOnlyTheEndHostingItWentOffline()
        {
            const int remoteProxyPort = 40231, destinationPort = 40232;
            const int tunnelTimeoutMs = 3000;

            var aWrites = Path.GetTempFileName();   // what B reads is a copy of this that can be held back
            var bReads = Path.GetTempFileName();
            var bWrites = Path.GetTempFileName();   // A reads this one directly

            using var link = new HeldBackCopy(aWrites, bReads);
            var a = new Thread(() => ft.Program.Main(StringUtility.CommandLineToArgs(
                $@"-R {remoteProxyPort} --tunnel-timeout {tunnelTimeoutMs} --write ""{aWrites}"" --read ""{bWrites}""")));
            var b = new Thread(() => ft.Program.Main(StringUtility.CommandLineToArgs(
                $@"--tunnel-timeout {tunnelTimeoutMs} --write ""{bWrites}"" --read ""{bReads}""")));

            var destination = new TcpListener(IPAddress.Loopback, destinationPort);
            destination.Start();

            try
            {
                a.Start();
                b.Start();

                Assert.IsTrue(ProxyReaches(remoteProxyPort, destination, TimeSpan.FromSeconds(30)), "The proxy B hosts for A never worked");

                // B hears nothing from A for longer than the tunnel timeout; A keeps hearing B.
                link.HoldBack(TimeSpan.FromMilliseconds(tunnelTimeoutMs * 2));

                Assert.IsTrue(ProxyReaches(remoteProxyPort, destination, TimeSpan.FromSeconds(20)), "The proxy B hosts for A did not come back");
            }
            finally
            {
                try { destination.Stop(); } catch { }
                a.Interrupt(); a.Join();
                b.Interrupt(); b.Join();
                ft.CLI.Options.TunnelTimeoutMilliseconds = ft.CLI.Options.DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS;
            }
        }

        // True once a SOCKS5 CONNECT through the proxy reaches the destination, retrying until the deadline.
        static bool ProxyReaches(int proxyPort, TcpListener destination, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, proxyPort);
                    var stream = client.GetStream();
                    stream.ReadTimeout = 10000;

                    stream.Write([0x05, 0x01, 0x00]);
                    if (Read(stream, 2)[1] != 0x00) continue;

                    var port = ((IPEndPoint)destination.LocalEndpoint).Port;
                    stream.Write([0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(port >> 8), (byte)port]);
                    if (Read(stream, 10)[1] != 0x00) continue;

                    using var accepted = destination.AcceptTcpClient();
                    return true;
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        static byte[] Read(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            for (var total = 0; total < count;)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) throw new EndOfStreamException();
                total += read;
            }
            return buffer;
        }

        // Keeps a copy of one file up to date every few milliseconds, except while held back: a link from
        // one end to the other that can go quiet without the reverse direction noticing. It starts copying
        // once the writer's session id is in place, so this test doesn't depend on the one above.
        sealed class HeldBackCopy : IDisposable
        {
            readonly CancellationTokenSource stop = new();
            DateTime heldUntil = DateTime.MinValue;

            public HeldBackCopy(string source, string copy)
            {
                new Thread(() =>
                {
                    byte[] last = [];
                    while (!stop.IsCancellationRequested)
                    {
                        Thread.Sleep(5);
                        if (DateTime.UtcNow < heldUntil) continue;
                        try
                        {
                            byte[] content;
                            using (var s = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                content = new byte[s.Length];
                                s.ReadExactly(content);
                            }
                            if (content.AsSpan().SequenceEqual(last)) continue;
                            if (content.Length < 8 || BitConverter.ToInt64(content, 0) == 0) continue;

                            using (var c = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            {
                                c.Write(content);
                                c.SetLength(content.Length);
                            }
                            last = content;
                        }
                        catch { }
                    }
                })
                { IsBackground = true }.Start();
            }

            public void HoldBack(TimeSpan duration)
            {
                heldUntil = DateTime.UtcNow + duration;
                Thread.Sleep(duration);
            }

            public void Dispose() => stop.Cancel();
        }
    }
}
