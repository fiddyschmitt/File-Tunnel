using ft.CLI;
using ft.Commands;
using ft.IO.Files;
using ft.Listeners;
using ft.Streams;
using ft.Tunnels;
using ft_tests.Utilities;
using System.Net;
using System.Net.Sockets;

namespace ft_tests
{
    /// <summary>
    /// How each end of a file tunnel recognises the other's session, and what it keeps across an outage:
    /// in-process tests over local temp files or an in-memory server, so no lab is needed.
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

        // The same over the upload-download transports, which dropped such proxies whenever they went
        // offline: they didn't tell the tunnel when the counterpart restarted, so keeping them would have
        // left a restarted counterpart's old ones open.
        [TestMethod]
        [Timeout(120000)]
        [DataRow(1, true)]    // one file each way, read blocking: FTP, WebDAV, S3, Dropbox
        [DataRow(5, false)]   // five files each way, read non-blocking: 9P
        public void UploadDownload_RemoteProxy_ComesBack_WhenOnlyTheEndHostingItWentOffline(int files, bool blockingReader)
        {
            const int remoteProxyPort = 40233;
            using var rig = new UploadDownloadRig(files, blockingReader);
            rig.StartEnd("a", remoteProxyPort);
            var b = rig.StartEnd("b");

            Assert.IsTrue(rig.ProxyReaches(remoteProxyPort), "The proxy B hosts for A never worked");

            // B sees nothing from A for longer than the tunnel timeout; A keeps seeing B.
            b.View.HoldBack(TimeSpan.FromMilliseconds(UploadDownloadRig.TunnelTimeoutMs * 2));

            Assert.IsTrue(rig.ProxyReaches(remoteProxyPort), "The proxy B hosts for A did not come back");
        }

        // An end that restarts within the tunnel timeout never takes the tunnel down, so the other end has
        // to recognise the new session by itself: it hosts the proxies the new one asks for, and no longer
        // the ones the old one asked for.
        [TestMethod]
        [Timeout(120000)]
        [DataRow(1, true)]
        [DataRow(5, false)]
        public void UploadDownload_EndRestartingQuickly_GetsOnlyTheProxiesItNowAsksFor(int files, bool blockingReader)
        {
            const int oldProxyPort = 40234, newProxyPort = 40235;
            using var rig = new UploadDownloadRig(files, blockingReader);
            var a = rig.StartEnd("a", oldProxyPort);
            rig.StartEnd("b");

            Assert.IsTrue(rig.ProxyReaches(oldProxyPort), "The proxy B hosts for A never worked");

            a.End();
            rig.StartEnd("a", newProxyPort);

            Assert.IsTrue(rig.ProxyReaches(newProxyPort), "B never hosted the proxy the restarted A asked for");
            Assert.IsFalse(Listening(oldProxyPort), "B still hosts the proxy A asked for before it restarted");
        }

        // Likewise for the end hosting the proxies: restarted, it has lost them, and the other end has to ask
        // for them again although the tunnel never went down.
        [TestMethod]
        [Timeout(120000)]
        [DataRow(1, true)]
        [DataRow(5, false)]
        public void UploadDownload_HostRestartingQuickly_HostsTheProxiesAgain(int files, bool blockingReader)
        {
            const int remoteProxyPort = 40236;
            using var rig = new UploadDownloadRig(files, blockingReader);
            rig.StartEnd("a", remoteProxyPort);
            var b = rig.StartEnd("b");

            Assert.IsTrue(rig.ProxyReaches(remoteProxyPort), "The proxy B hosts for A never worked");

            b.End();
            rig.StartEnd("b");

            Assert.IsTrue(rig.ProxyReaches(remoteProxyPort), "The restarted B never hosted the proxy A asked for");
        }

        // An upload-download end learns of a restart from the session id in each of the counterpart's files.
        // A file the old session left behind, say where a delete failed as the new session started, can turn
        // up after the new session's first. That is no new restart: taken for one, it would drop the proxies
        // the new session had just asked for, and the new session would never ask again.
        [TestMethod]
        [Timeout(60000)]
        public void UploadDownload_FileLeftByTheOldSession_IsNoNewRestart()
        {
            using var rig = new UploadDownloadRig(1, true);
            var view = new ServerView(rig.Server, "a");
            var reader = new UploadDownload(view, "a.dat", "b.dat", UploadDownloadRig.TunnelTimeoutMs, 1, 0, true, false);
            var sessions = 0;
            reader.SessionChanged += (s, e) => Interlocked.Increment(ref sessions);
            new Thread(reader.ReceivePump) { IsBackground = true }.Start();

            try
            {
                foreach (var sessionId in new long[] { 1, 2, 1 })
                {
                    WriteUploadDownloadFile(rig.Server, "a.dat", sessionId);
                    Assert.IsTrue(Taken(rig.Server, "a.dat", TimeSpan.FromSeconds(10)), $"The reader never took the file from session {sessionId}");
                }

                Assert.AreEqual(2, sessions, "The reader took the old session's leftover file for another restart");
            }
            finally
            {
                view.Cut();
            }
        }

        // A file as an upload-download writer writes one: its session id and the time it was written, then
        // a command.
        static void WriteUploadDownloadFile(MemoryServer server, string path, long sessionId)
        {
            using var content = new MemoryStream();
            var writer = new BinaryWriter(new HashingStream(content, false, UploadDownloadRig.TunnelTimeoutMs));
            writer.Write(sessionId);
            writer.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            new Ping(EnumPingType.Request).Serialise(writer);
            writer.Flush();
            lock (server.Files) server.Files[path] = content.ToArray();
        }

        // True once the reader has processed the file and deleted it.
        static bool Taken(MemoryServer server, string path, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                lock (server.Files)
                {
                    if (!server.Files.ContainsKey(path)) return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }

        static bool Listening(int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException)
            {
                return false;
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

        // Both ends of an upload-download tunnel in this process, over an in-memory server, and a destination
        // for the proxies between them to reach. Ends are "a" and "b", and each reads the other's files.
        sealed class UploadDownloadRig : IDisposable
        {
            public const int TunnelTimeoutMs = 3000;

            public MemoryServer Server { get; } = new();

            readonly int files;
            readonly bool blockingReader;
            readonly TcpListener destination = new(IPAddress.Loopback, 0);
            readonly List<UploadDownloadEnd> ends = [];
            readonly (int, int, int) savedOptions = (Options.PaceMilliseconds, Options.WriteIntervalMilliseconds, Options.ReadIntervalMilliseconds);

            public UploadDownloadRig(int files, bool blockingReader)
            {
                this.files = files;
                this.blockingReader = blockingReader;

                // A pace that doesn't spin on the in-memory server, and no write or read interval.
                (Options.PaceMilliseconds, Options.WriteIntervalMilliseconds, Options.ReadIntervalMilliseconds) = (10, 0, 0);
                destination.Start();
            }

            // remoteProxyPorts are the end's -R SOCKS proxies, which the other end hosts.
            public UploadDownloadEnd StartEnd(string name, params int[] remoteProxyPorts)
            {
                var end = new UploadDownloadEnd(Server, name, name == "a" ? "b" : "a", files, blockingReader, remoteProxyPorts);
                ends.Add(end);
                return end;
            }

            public bool ProxyReaches(int port) => SessionUnitTests.ProxyReaches(port, destination, TimeSpan.FromSeconds(30));

            public void Dispose()
            {
                ends.ForEach(end => end.End());
                destination.Stop();
                (Options.PaceMilliseconds, Options.WriteIntervalMilliseconds, Options.ReadIntervalMilliseconds) = savedOptions;
            }
        }

        // One end, wired as Program.RunSession wires one.
        sealed class UploadDownloadEnd
        {
            public ServerView View { get; }
            readonly MultiServer listeners = new();

            public UploadDownloadEnd(MemoryServer server, string name, string counterpart, int files, bool blockingReader, int[] remoteProxyPorts)
            {
                const int maxFileSizeBytes = 64 * 1024, readDurationMillis = 50;

                View = new ServerView(server, counterpart);
                var manager = new UploadDownload(View, $"{counterpart}.dat", $"{name}.dat", UploadDownloadRig.TunnelTimeoutMs, files, maxFileSizeBytes, blockingReader, false);
                var localToRemoteTunnel = new LocalToRemoteTunnel(listeners, manager, maxFileSizeBytes, readDurationMillis);
                _ = new RemoteToLocalTunnel([.. remoteProxyPorts.Select(port => $"{port}")], [], [], manager, localToRemoteTunnel, "0.0.0.0", maxFileSizeBytes, readDurationMillis, UploadDownloadRig.TunnelTimeoutMs);
                manager.Start();
            }

            // As if its process ended: it no longer reaches the server, and its listeners close. Its threads
            // stay parked on the server for good.
            public void End()
            {
                View.Cut();
                listeners.Stop("The test ended this end.");
            }
        }

        sealed class MemoryServer
        {
            public readonly Dictionary<string, byte[]> Files = [];
        }

        // One end's view of the server. Held back, it can't see the counterpart's files for a while, though
        // the counterpart still sees its own. Cut, it blocks every call for good; once Cut returns, nothing
        // more from that end reaches the server.
        sealed class ServerView(MemoryServer server, string counterpart) : IFileAccess
        {
            DateTime heldUntil = DateTime.MinValue;
            bool cut;

            public void HoldBack(TimeSpan duration)
            {
                lock (server.Files) heldUntil = DateTime.UtcNow + duration;
                Thread.Sleep(duration);
            }

            public void Cut()
            {
                lock (server.Files) cut = true;
            }

            T Call<T>(Func<Dictionary<string, byte[]>, T> operation)
            {
                lock (server.Files)
                {
                    if (!cut) return operation(server.Files);
                }
                Thread.Sleep(Timeout.Infinite);
                return default!;
            }

            bool Hidden(string path) => path.StartsWith($"{counterpart}.") && DateTime.UtcNow < heldUntil;

            byte[] Content(Dictionary<string, byte[]> files, string path) =>
                !Hidden(path) && files.TryGetValue(path, out var content) ? content : throw new FileNotFoundException(path);

            public bool Exists(string path) => Call(files => !Hidden(path) && files.ContainsKey(path));

            public void Delete(string path) => Call(files => !Hidden(path) && files.Remove(path));

            public void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes, bool overwrite = true) => Call(files => files[path] = bytes.ToArray());

            public void Move(string sourceFileName, string destFileName, bool overwrite) =>
                Call(files => files[destFileName] = files.Remove(sourceFileName, out var content) ? content : throw new FileNotFoundException(sourceFileName));

            public byte[] ReadAllBytes(string path) => Call(files => Content(files, path).ToArray());

            public long GetFileSize(string path) => Call(files => (long)Content(files, path).Length);
        }
    }
}
