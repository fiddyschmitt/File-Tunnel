using ft_tests.Utilities;
using System;
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
    // In-process end-to-end tests for SOCKS dynamic forwarding (ft's -D / -R). Spins up two ft.Program.Main
    // instances on threads over temp files, drives a real SOCKS client through the tunnel to a loopback
    // "ultimate destination", and verifies byte integrity both ways - the same hermetic pattern as
    // TcpUnitTests. [Timeout] bounds any hang.
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class SocksUnitTests
    {
        const int PayloadBytes = 256 * 1024;

        [DataTestMethod]
        [Timeout(180000)]
        [DataRow((byte)5, false, 55101, 58101, DisplayName = "LocalDynamic_Socks5_IPv4")]
        [DataRow((byte)5, true, 55102, 58102, DisplayName = "LocalDynamic_Socks5_Domain")]   // far-side name resolution
        [DataRow((byte)4, false, 55103, 58103, DisplayName = "LocalDynamic_Socks4_IPv4")]
        public void LocalDynamicForward(byte version, bool useHostname, int socksPort, int destPort)
        {
            RunDynamicForwardTransfer($"-D {socksPort}", socksPort, version, useHostname, destPort);
        }

        [TestMethod]
        [Timeout(180000)]
        public void RemoteDynamicForward_Socks5_IPv4()
        {
            // "-R port" (no destination) overloads into a remote SOCKS proxy. The SOCKS server runs on the
            // far side; this exercises the CreateListener("socks", ...) + MultiServer partition path.
            RunDynamicForwardTransfer("-R 55111", 55111, version: 5, useHostname: false, destPort: 58111);
        }

        [TestMethod]
        [Timeout(90000)]   // the dialer retries for the ~10s tunnel-timeout before reporting the refusal
        public void FailureReply_RefusedDestination_ReportsAccurateCode()
        {
            const int socksPort = 55121;
            const int deadPort = 55199;   // nothing listens here

            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();

            var (listenThread, forwardThread) = StartTunnel($"-D {socksPort}", writeFilename, readFilename);

            try
            {
                using var origin = ConnectToSocksPort(socksPort);
                var stream = origin.GetStream();
                stream.ReadTimeout = 60000;

                // SOCKS5 CONNECT to a dead port.
                stream.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var methodSelect = ReadExactly(stream, 2);
                Assert.AreEqual((byte)0x00, methodSelect[1], "server should select no-auth");

                var request = new List<byte> { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(deadPort >> 8), (byte)(deadPort & 0xFF) };
                stream.Write(request.ToArray(), 0, request.Count);

                var reply = ReadExactly(stream, 10);

                // The accurate-reply contract: a refused dial yields a FAILURE reply code (not optimistic
                // success followed by a silent drop).
                Assert.AreEqual((byte)0x05, reply[0], "SOCKS5 reply version");
                Assert.AreNotEqual((byte)0x00, reply[1], "expected a SOCKS failure reply code for a refused destination");
            }
            finally
            {
                Teardown(null, null, listenThread, forwardThread, readFilename, writeFilename);
            }
        }

        // ---- harness ---------------------------------------------------------------------------------

        static void RunDynamicForwardTransfer(string dynamicArg, int socksPort, byte version, bool useHostname, int destPort)
        {
            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();

            var (listenThread, forwardThread) = StartTunnel(dynamicArg, writeFilename, readFilename);

            // Dual-stack so "localhost" resolving to either ::1 or 127.0.0.1 on the exit side still connects.
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
                var host = useHostname ? "localhost" : "127.0.0.1";
                origin = ConnectSocks(version, socksPort, host, destPort);

                Assert.IsTrue(acceptedClients.TryTake(out var destClient, 30000),
                    "The exit side never dialed the ultimate destination");

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

        static (Thread Listen, Thread Forward) StartTunnel(string dynamicArg, string writeFilename, string readFilename)
        {
            var listenThread = new Thread(() =>
            {
                var args = StringUtility.CommandLineToArgs($@"{dynamicArg} --write ""{writeFilename}"" --read ""{readFilename}""");
                ft.Program.Main(args);
            });
            listenThread.Start();

            var forwardThread = new Thread(() =>
            {
                var args = StringUtility.CommandLineToArgs($@"--read ""{writeFilename}"" --write ""{readFilename}""");
                ft.Program.Main(args);
            });
            forwardThread.Start();

            return (listenThread, forwardThread);
        }

        static void Teardown(TcpClient? origin, Thread? acceptThread, Thread listenThread, Thread forwardThread, string readFilename, string writeFilename)
        {
            try { origin?.Close(); } catch { }
            try { acceptThread?.Join(2000); } catch { }

            listenThread.Interrupt();
            listenThread.Join();
            forwardThread.Interrupt();
            forwardThread.Join();

            try { File.Delete(readFilename); } catch { }
            try { File.Delete(writeFilename); } catch { }
        }

        // ---- minimal SOCKS client --------------------------------------------------------------------

        static TcpClient ConnectSocks(byte version, int socksPort, string host, int port)
        {
            var client = ConnectToSocksPort(socksPort);
            var stream = client.GetStream();
            stream.ReadTimeout = 60000;

            if (version == 5)
            {
                stream.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);   // greeting: no-auth
                var methodSelect = ReadExactly(stream, 2);
                Assert.AreEqual((byte)0x05, methodSelect[0], "SOCKS5 method-select version");
                Assert.AreEqual((byte)0x00, methodSelect[1], "SOCKS5 server did not select no-auth");

                var request = new List<byte> { 0x05, 0x01, 0x00 };   // VER, CONNECT, RSV
                if (IPAddress.TryParse(host, out var ip))
                {
                    request.Add(0x01);
                    request.AddRange(ip.GetAddressBytes());
                }
                else
                {
                    var hostBytes = Encoding.ASCII.GetBytes(host);
                    request.Add(0x03);
                    request.Add((byte)hostBytes.Length);
                    request.AddRange(hostBytes);
                }
                request.Add((byte)(port >> 8));
                request.Add((byte)(port & 0xFF));
                stream.Write(request.ToArray(), 0, request.Count);

                var reply = ReadExactly(stream, 10);
                Assert.AreEqual((byte)0x05, reply[0], "SOCKS5 reply version");
                Assert.AreEqual((byte)0x00, reply[1], $"SOCKS5 CONNECT failed (REP 0x{reply[1]:x2})");
            }
            else   // SOCKS4 (IPv4 literal in these tests)
            {
                var ipBytes = IPAddress.Parse(host).GetAddressBytes();
                var request = new List<byte> { 0x04, 0x01, (byte)(port >> 8), (byte)(port & 0xFF) };
                request.AddRange(ipBytes);
                request.Add(0x00);   // empty USERID
                stream.Write(request.ToArray(), 0, request.Count);

                var reply = ReadExactly(stream, 8);
                Assert.AreEqual((byte)0x00, reply[0], "SOCKS4 reply version byte");
                Assert.AreEqual((byte)0x5A, reply[1], $"SOCKS4 CONNECT failed (CD 0x{reply[1]:x2})");
            }

            stream.ReadTimeout = Timeout.Infinite;   // transfers below have no per-read deadline
            return client;
        }

        static TcpClient ConnectToSocksPort(int socksPort)
        {
            var client = new TcpClient();
            var start = DateTime.Now;
            while (true)
            {
                if ((DateTime.Now - start).TotalSeconds > 25)
                {
                    throw new Exception($"Could not connect to SOCKS port {socksPort}");
                }
                try { client.Connect(IPAddress.Loopback, socksPort); break; }
                catch { Thread.Sleep(200); }
            }
            return client;
        }

        static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) throw new EndOfStreamException("SOCKS reply truncated");
                total += read;
            }
            return buffer;
        }
    }
}
