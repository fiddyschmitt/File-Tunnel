using ft_tests.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ft_tests
{
    // In-process end-to-end tests for the HTTP CONNECT proxy (ft's --http-proxy / --remote-http-proxy),
    // mirroring SocksUnitTests: two ft.Program.Main instances over temp files, a real CONNECT client through
    // the tunnel to a loopback destination, byte-integrity both ways. [Timeout] bounds any hang.
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class HttpProxyUnitTests
    {
        const int PayloadBytes = 256 * 1024;
        const int StressPayloadBytes = 8 * 1024;

        [DataTestMethod]
        [Timeout(180000)]
        [DataRow("--http-proxy 0.0.0.0:57101", 57101, 58101, DisplayName = "Local_HttpProxy")]
        [DataRow("--remote-http-proxy 0.0.0.0:57102", 57102, 58102, DisplayName = "Remote_HttpProxy")]
        public void HttpProxy_Connect_Transfers(string proxyArg, int proxyPort, int destPort)
        {
            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();
            var (listenThread, forwardThread) = StartTunnel(proxyArg, writeFilename, readFilename);

            var ultimateDestination = TcpListener.Create(destPort);
            ultimateDestination.Start();
            var acceptedClients = new BlockingCollection<TcpClient>();
            var acceptCts = new CancellationTokenSource();
            var acceptThread = new Thread(() =>
            {
                while (!acceptCts.IsCancellationRequested)
                {
                    try { acceptedClients.Add(ultimateDestination.AcceptTcpClient()); }
                    catch { break; }
                }
            })
            { IsBackground = true };
            acceptThread.Start();

            TcpClient? origin = null;
            try
            {
                origin = ConnectHttp(proxyPort, "127.0.0.1", destPort);
                Assert.IsTrue(acceptedClients.TryTake(out var destClient, 30000), "The exit side never dialed the destination");

                var payload = new byte[PayloadBytes];
                Random.Shared.NextBytes(payload);
                TransferVerification.TestDirection("Forward", origin, destClient!, payload);
                TransferVerification.TestDirection("Reverse", destClient!, origin, payload);
            }
            finally
            {
                acceptCts.Cancel();
                try { ultimateDestination.Stop(); } catch { }
                Teardown(origin, acceptThread, listenThread, forwardThread, readFilename, writeFilename);
            }
        }

        [TestMethod]
        [Timeout(90000)]   // the dialer retries for the ~10s tunnel-timeout before reporting the failure
        public void HttpProxy_DeadDestination_Returns5xx()
        {
            const int proxyPort = 57111, deadPort = 57199;
            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();
            var (listenThread, forwardThread) = StartTunnel($"--http-proxy 0.0.0.0:{proxyPort}", writeFilename, readFilename);

            try
            {
                using var proxy = ConnectToProxyPort(proxyPort);
                var stream = proxy.GetStream();
                stream.ReadTimeout = 60000;

                var req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{deadPort} HTTP/1.1\r\nHost: 127.0.0.1:{deadPort}\r\n\r\n");
                stream.Write(req, 0, req.Length);

                var statusLine = ReadStatusLine(stream);
                Assert.IsTrue(statusLine.StartsWith("HTTP/1.1 5"), $"Expected a 5xx failure for a dead destination, got: {statusLine}");
            }
            finally
            {
                Teardown(null, null, listenThread, forwardThread, readFilename, writeFilename);
            }
        }

        // Stress: BOTH ft instances host a local (--http-proxy) AND a remote (--remote-http-proxy) HTTP proxy,
        // so four CONNECT proxies share one file tunnel - two on each side (its own --http-proxy plus the other
        // side's --remote-http-proxy). Hundreds of short-lived CONNECT requests are then fired in quick
        // succession across all four proxies; each dials a single loopback echo destination, sends a unique
        // payload (its first bytes carry the request id) and requires exactly those bytes back. A handshake,
        // multiplexing, or teardown bug under rapid churn would drop a request, splice two connections' bytes,
        // or leak a connection - each of which fails the test (a recorded request failure, an echo mismatch, or
        // a wrong exit-side dial count).
        [TestMethod]
        [Timeout(300000)]
        public void HttpProxyStress_FourProxies_HundredsOfRapidRequests()
        {
            const int aLocal = 57210, aRemote = 57211, bLocal = 57212, bRemote = 57213, dest = 57214;
            const int totalRequests = 400;
            const int concurrency = 32;

            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();

            // A hosts --http-proxy aLocal (exit = B) and --remote-http-proxy aRemote (proxy on B, exit = A);
            // B hosts --http-proxy bLocal (exit = A) and --remote-http-proxy bRemote (proxy on A, exit = B).
            var (threadA, threadB) = StartDualHttpProxyTunnel(
                $"--http-proxy 0.0.0.0:{aLocal} --remote-http-proxy 0.0.0.0:{aRemote}",
                $"--http-proxy 0.0.0.0:{bLocal} --remote-http-proxy 0.0.0.0:{bRemote}",
                writeFilename, readFilename);

            // One loopback echo server that every CONNECT is pointed at (both ft instances dial it). Each
            // accepted connection echoes its bytes back, so every request self-verifies without pairing.
            var echoListener = new TcpListener(IPAddress.Loopback, dest);
            echoListener.Start(200);
            var dialCount = 0;
            var acceptCts = new CancellationTokenSource();
            var acceptThread = new Thread(() =>
            {
                while (!acceptCts.IsCancellationRequested)
                {
                    TcpClient destClient;
                    try { destClient = echoListener.AcceptTcpClient(); }
                    catch { break; }
                    Interlocked.Increment(ref dialCount);
                    new Thread(() => EchoUntilClosed(destClient)) { IsBackground = true }.Start();
                }
            })
            { IsBackground = true };
            acceptThread.Start();

            var proxyPorts = new[] { aLocal, aRemote, bLocal, bRemote };
            var failures = new ConcurrentBag<string>();

            try
            {
                // Gate on every proxy being live first (the two --remote-http-proxy listeners only appear once
                // the tunnel is online and the CreateListener commands have crossed), so the burst below isn't
                // just workers spinning in the connect-retry loop.
                foreach (var proxyPort in proxyPorts) WaitForProxyReady(proxyPort);

                // Fire the requests: `concurrency` workers each pull ids off a shared counter and run them
                // back-to-back, so hundreds of CONNECTs hit the four proxies in quick succession.
                var nextId = -1;
                var workers = new List<Thread>();
                for (var w = 0; w < concurrency; w++)
                {
                    var worker = new Thread(() =>
                    {
                        while (true)
                        {
                            var id = Interlocked.Increment(ref nextId);
                            if (id >= totalRequests) break;

                            var proxyPort = proxyPorts[id % proxyPorts.Length];
                            try { PerformHttpEchoRequest(proxyPort, dest, id); }
                            catch (Exception ex) { failures.Add($"request {id} via proxy {proxyPort}: {ex.Message}"); }
                        }
                    })
                    { IsBackground = true };
                    workers.Add(worker);
                    worker.Start();
                }
                foreach (var worker in workers) worker.Join();

                Assert.AreEqual(0, failures.Count,
                    $"{failures.Count}/{totalRequests} HTTP CONNECT requests failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
                Assert.AreEqual(totalRequests, Volatile.Read(ref dialCount),
                    "the exit side should have dialed the destination exactly once per request");
            }
            finally
            {
                acceptCts.Cancel();
                try { echoListener.Stop(); } catch { }
                threadA.Interrupt(); threadA.Join();
                threadB.Interrupt(); threadB.Join();
                try { File.Delete(readFilename); } catch { }
                try { File.Delete(writeFilename); } catch { }
            }
        }

        // ---- harness -----------------------------------------------------------------------------------

        static (Thread A, Thread B) StartDualHttpProxyTunnel(string aArgs, string bArgs, string writeFilename, string readFilename)
        {
            var threadA = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"{aArgs} --write ""{writeFilename}"" --read ""{readFilename}""")));
            threadA.Start();

            var threadB = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"{bArgs} --read ""{writeFilename}"" --write ""{readFilename}""")));
            threadB.Start();

            return (threadA, threadB);
        }

        static (Thread Listen, Thread Forward) StartTunnel(string proxyArg, string writeFilename, string readFilename)
        {
            var listenThread = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"{proxyArg} --write ""{writeFilename}"" --read ""{readFilename}""")));
            listenThread.Start();

            var forwardThread = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"--read ""{writeFilename}"" --write ""{readFilename}""")));
            forwardThread.Start();

            return (listenThread, forwardThread);
        }

        static void Teardown(TcpClient? origin, Thread? acceptThread, Thread listenThread, Thread forwardThread, string readFilename, string writeFilename)
        {
            try { origin?.Close(); } catch { }
            try { acceptThread?.Join(2000); } catch { }
            listenThread.Interrupt(); listenThread.Join();
            forwardThread.Interrupt(); forwardThread.Join();
            try { File.Delete(readFilename); } catch { }
            try { File.Delete(writeFilename); } catch { }
        }

        // Sends CONNECT dest:port, verifies a 200, consumes the reply headers, returns the now-tunnelled client.
        static TcpClient ConnectHttp(int proxyPort, string destHost, int destPort)
        {
            var client = ConnectToProxyPort(proxyPort);
            var stream = client.GetStream();
            stream.ReadTimeout = 60000;

            var req = Encoding.ASCII.GetBytes($"CONNECT {destHost}:{destPort} HTTP/1.1\r\nHost: {destHost}:{destPort}\r\n\r\n");
            stream.Write(req, 0, req.Length);

            var statusLine = ReadStatusLine(stream);
            Assert.IsTrue(statusLine.StartsWith("HTTP/1.1 200"), $"CONNECT failed: {statusLine}");
            DrainToBlankLine(stream);   // consume the rest of the reply so the stream is a clean tunnel

            stream.ReadTimeout = Timeout.Infinite;
            return client;
        }

        static TcpClient ConnectToProxyPort(int proxyPort)
        {
            var client = new TcpClient();
            var start = DateTime.Now;
            while (true)
            {
                if ((DateTime.Now - start).TotalSeconds > 25) throw new Exception($"Could not connect to HTTP proxy port {proxyPort}");
                try { client.Connect(IPAddress.Loopback, proxyPort); break; }
                catch { Thread.Sleep(200); }
            }
            return client;
        }

        // Reads the HTTP status line (through, but excluding, its trailing CRLF).
        static string ReadStatusLine(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var prev = -1;
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException("proxy reply truncated");
                if (prev == '\r' && b == '\n') { sb.Length--; break; }   // strip the trailing \r, stop at CRLF
                sb.Append((char)b);
                prev = b;
            }
            return sb.ToString();
        }

        // The status line's CRLF is already consumed (matched == 2); read to the end of the CRLFCRLF terminator.
        static void DrainToBlankLine(NetworkStream stream)
        {
            var matched = 2;
            while (matched < 4)
            {
                var b = stream.ReadByte();
                if (b < 0) return;
                if (b == '\r') matched = matched == 2 ? 3 : 1;
                else if (b == '\n') matched = matched == 1 ? 2 : matched == 3 ? 4 : 0;
                else matched = 0;
            }
        }

        // One full CONNECT round-trip: dial the proxy, CONNECT to the echo destination, send a unique payload
        // (its first 4 bytes are the request id, so a splice between two connections is detectable), and require
        // the exact same bytes back. Throws on any deviation so the caller records it as a failure.
        static void PerformHttpEchoRequest(int proxyPort, int destPort, int requestId)
        {
            using var client = ConnectToProxyPort(proxyPort);
            var stream = client.GetStream();
            stream.ReadTimeout = 30000;

            var req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{destPort} HTTP/1.1\r\nHost: 127.0.0.1:{destPort}\r\n\r\n");
            stream.Write(req, 0, req.Length);

            var statusLine = ReadStatusLine(stream);
            if (!statusLine.StartsWith("HTTP/1.1 200")) throw new Exception($"CONNECT rejected: {statusLine}");
            DrainToBlankLine(stream);

            var payload = new byte[StressPayloadBytes];
            Random.Shared.NextBytes(payload);
            BinaryPrimitives.WriteInt32BigEndian(payload, requestId);

            stream.Write(payload, 0, payload.Length);
            var echoed = ReadExactly(stream, payload.Length);
            if (!payload.AsSpan().SequenceEqual(echoed)) throw new Exception("echoed bytes differ from sent payload");
        }

        // Per-connection echo used by the stress destination: copy every byte back until the peer closes.
        static void EchoUntilClosed(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) stream.Write(buffer, 0, read);
                }
            }
            catch { }
        }

        static void WaitForProxyReady(int proxyPort)
        {
            var start = DateTime.Now;
            while (true)
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, proxyPort); return; }
                catch
                {
                    if ((DateTime.Now - start).TotalSeconds > 30) throw new Exception($"HTTP proxy port {proxyPort} never came up");
                    Thread.Sleep(200);
                }
            }
        }

        static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) throw new EndOfStreamException("proxy stream truncated");
                total += read;
            }
            return buffer;
        }
    }
}
