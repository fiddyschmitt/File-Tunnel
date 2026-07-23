using ft.Socks;
using ft_tests.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft_tests
{
    // Tests for SOCKS5 UDP ASSOCIATE (RFC 1928): the datagram-header codec + the passive per-destination
    // stream (pure, no network) and an in-process UDP-echo round-trip through a real `-D` tunnel.
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class SocksUdpUnitTests
    {
        // ---- datagram header codec (pure) ------------------------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_IPv4()
        {
            var payload = Encoding.ASCII.GetBytes("hello");
            var datagram = BuildUdpDatagram(0, 0x01, [1, 2, 3, 4], 8080, payload);

            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));
            Assert.AreEqual((byte)0, p.Frag);
            Assert.AreEqual("udp://1.2.3.4:8080", p.DestinationString);
            CollectionAssert.AreEqual(payload, p.ExtractData());
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0x01, 1, 2, 3, 4, 0x1F, 0x90 }, p.ReplyHeaderPrefix);
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_Domain()
        {
            var payload = Encoding.ASCII.GetBytes("q");
            var host = Encoding.ASCII.GetBytes("example.com");
            var datagram = BuildUdpDatagram(0, 0x03, host, 53, payload);

            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));
            Assert.AreEqual("udp://example.com:53", p.DestinationString);
            CollectionAssert.AreEqual(payload, p.ExtractData());
            // reply prefix echoes ATYP+len+host+port verbatim
            CollectionAssert.AreEqual(
                new byte[] { 0, 0, 0, 0x03, (byte)host.Length }.Concat(host).Concat(new byte[] { 0x00, 0x35 }).ToArray(),
                p.ReplyHeaderPrefix);
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_IPv6_Bracketed()
        {
            var v6 = new byte[16]; v6[15] = 1;   // ::1
            var datagram = BuildUdpDatagram(0, 0x04, v6, 80, [9]);

            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));
            Assert.AreEqual("udp://[::1]:80", p.DestinationString);
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_BigEndianPort()
        {
            var datagram = BuildUdpDatagram(0, 0x01, [1, 2, 3, 4], 0x1234, [0]);
            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));
            Assert.AreEqual("udp://1.2.3.4:4660", p.DestinationString);   // 0x1234 == 4660
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_FragNonZero_ParsedButFlagged()
        {
            var datagram = BuildUdpDatagram(0x01, 0x01, [1, 2, 3, 4], 8080, [1, 2]);
            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));
            Assert.AreEqual((byte)0x01, p.Frag);   // caller drops when Frag != 0
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_Truncated_ReturnsFalse()
        {
            Assert.IsFalse(SocksNegotiator.TryParseUdpDatagram([0, 0, 0, 0x01, 1, 2], out _));   // IPv4 addr cut short
            Assert.IsFalse(SocksNegotiator.TryParseUdpDatagram([0, 0], out _));                   // shorter than the fixed header
        }

        [TestMethod]
        [Timeout(15000)]
        public void ParseUdpDatagram_ReplyPrefix_RoundTrips()
        {
            var payload = Encoding.ASCII.GetBytes("payload");
            var datagram = BuildUdpDatagram(0, 0x01, [10, 0, 0, 9], 5353, payload);
            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(datagram, out var p));

            // A reply datagram (prefix + data) parses back to the same destination + data.
            var reply = p.ReplyHeaderPrefix.Concat(p.ExtractData()).ToArray();
            Assert.IsTrue(SocksNegotiator.TryParseUdpDatagram(reply, out var p2));
            Assert.AreEqual(p.DestinationString, p2.DestinationString);
            CollectionAssert.AreEqual(payload, p2.ExtractData());
        }

        // ---- ReadSocks5 UDP-associate parsing (stream) -----------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public void ReadSocks5_UdpAssociate_Wildcard_LearnsFromDatagram()
        {
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }));   // cmd=UDP ASSOCIATE, DST 0.0.0.0:0

            var req = SocksNegotiator.Read(stream);
            Assert.AreEqual(SocksCommand.UdpAssociate, req.Command);
            Assert.IsNull(req.UdpClientDeclaredEndpoint);                    // wildcard → learn from first datagram
            CollectionAssert.AreEqual(new byte[] { 0x05, 0x00 }, stream.Written());   // only method-select; no reply here
        }

        [TestMethod]
        [Timeout(15000)]
        public void ReadSocks5_UdpAssociate_ConcreteDeclaredSource()
        {
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x03, 0x00, 0x01, 10, 0, 0, 5, 0x04, 0xD2 }));   // DST 10.0.0.5:1234

            var req = SocksNegotiator.Read(stream);
            Assert.AreEqual(SocksCommand.UdpAssociate, req.Command);
            Assert.IsNotNull(req.UdpClientDeclaredEndpoint);
            Assert.AreEqual("10.0.0.5:1234", req.UdpClientDeclaredEndpoint!.ToString());
        }

        [TestMethod]
        [Timeout(15000)]
        public void ReadSocks5_Bind_StillRejected()
        {
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x02, 0x00, 0x01, 1, 2, 3, 4, 0x00, 0x50 }));   // cmd=BIND

            Assert.ThrowsExactly<SocksException>(() => SocksNegotiator.Read(stream));
            CollectionAssert.AreEqual(
                new byte[] { 0x05, 0x00, 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },
                stream.Written());
        }

        // ---- SocksUdpStream contract (pure, stub sink) -----------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public void SocksUdpStream_ReadsPerDatagram_WritesWithPrefix_ClosesCleanly()
        {
            var sink = new CapturingSink();
            var prefix = new byte[] { 0, 0, 0, 0x01, 1, 2, 3, 4, 0x1F, 0x90 };
            var s = new SocksUdpStream(sink, "udp://1.2.3.4:8080", prefix);

            // one queued datagram per Read (boundary preserved)
            s.AddToReadQueue([1, 2, 3]);
            s.AddToReadQueue([4, 5]);
            var buf = new byte[65535];
            Assert.AreEqual(3, s.Read(buf, 0, buf.Length));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, buf.Take(3).ToArray());
            Assert.AreEqual(2, s.Read(buf, 0, buf.Length));
            CollectionAssert.AreEqual(new byte[] { 4, 5 }, buf.Take(2).ToArray());

            // Write prepends the reply prefix and hands one packet to the sink
            s.Write([9, 9], 0, 2);
            Assert.AreEqual(1, sink.Sent.Count);
            CollectionAssert.AreEqual(prefix.Concat(new byte[] { 9, 9 }).ToArray(), sink.Sent[0]);

            // Close unblocks a pending Read (returns 0 = EOF) and de-registers from the sink
            var pending = Task.Run(() => s.Read(buf, 0, buf.Length));
            Thread.Sleep(150);
            s.Close();
            Assert.IsTrue(pending.Wait(5000));
            Assert.AreEqual(0, pending.Result);
            CollectionAssert.Contains(sink.Removed, "udp://1.2.3.4:8080");
        }

        // ---- hermetic UDP-echo through a real -D tunnel ----------------------------------------------

        // NB: the e2e rows stay on IPv4 destinations. ft's far-side UDP dial binds --udp-send-from (default
        // 0.0.0.0, an IPv4 socket), so an IPv6 *destination* is a pre-existing ft limitation, not a SOCKS
        // one - IPv6 datagram parsing is covered by ParseUdpDatagram_IPv6_Bracketed. The domain row uses an
        // IP in domain (ATYP 0x03) form to exercise the domain codec without resolver-order flakiness (real
        // far-side hostname resolution is already covered by the TCP Socks5_Domain test).
        [DataTestMethod]
        [Timeout(180000)]
        [DataRow((byte)0x01, "127.0.0.1", 56101, 59101, DisplayName = "Udp_IPv4")]
        [DataRow((byte)0x03, "127.0.0.1", 56102, 59102, DisplayName = "Udp_DomainAtyp")]
        public void LocalUdpAssociate_Echo(byte atyp, string host, int socksPort, int destPort)
        {
            var payload = new byte[4096];
            Random.Shared.NextBytes(payload);

            RunUdpTunnel(socksPort, [destPort], (control, relaySocket, bnd, echoPorts) =>
            {
                var addr = AddressBytes(atyp, host);
                var got = SendAndReceive(relaySocket, bnd, BuildRequestDatagram(0, atyp, addr, destPort, payload), out var headerLen);
                CollectionAssert.AreEqual(payload, got.Skip(headerLen).ToArray());
            });
        }

        [TestMethod]
        [Timeout(180000)]
        public void LocalUdpAssociate_MultipleDestinations_OneAssociation()
        {
            int socksPort = 56111, destA = 59111, destB = 59112;
            var payloadA = Encoding.ASCII.GetBytes("to-A");
            var payloadB = Encoding.ASCII.GetBytes("destination-B");

            RunUdpTunnel(socksPort, [destA, destB], (control, relaySocket, bnd, echoPorts) =>
            {
                var gotA = SendAndReceive(relaySocket, bnd, BuildRequestDatagram(0, 0x01, [127, 0, 0, 1], destA, payloadA), out var hlA);
                CollectionAssert.AreEqual(payloadA, gotA.Skip(hlA).ToArray());

                var gotB = SendAndReceive(relaySocket, bnd, BuildRequestDatagram(0, 0x01, [127, 0, 0, 1], destB, payloadB), out var hlB);
                CollectionAssert.AreEqual(payloadB, gotB.Skip(hlB).ToArray());
            });
        }

        [TestMethod]
        [Timeout(180000)]
        public void LocalUdpAssociate_LargeDatagram_NotSplitOrCoalesced()
        {
            int socksPort = 56121, destPort = 59121;
            var payload = new byte[60000];
            Random.Shared.NextBytes(payload);

            RunUdpTunnel(socksPort, [destPort], (control, relaySocket, bnd, echoPorts) =>
            {
                var got = SendAndReceive(relaySocket, bnd, BuildRequestDatagram(0, 0x01, [127, 0, 0, 1], destPort, payload), out var headerLen);
                CollectionAssert.AreEqual(payload, got.Skip(headerLen).ToArray());
            });
        }

        [TestMethod]
        [Timeout(180000)]
        public void LocalUdpAssociate_FragmentedDatagram_Dropped()
        {
            int socksPort = 56131, destPort = 59131;
            var payload = Encoding.ASCII.GetBytes("frag");

            RunUdpTunnel(socksPort, [destPort], (control, relaySocket, bnd, echoPorts) =>
            {
                relaySocket.Client.ReceiveTimeout = 3000;
                relaySocket.Send(BuildRequestDatagram(0x01 /* FRAG!=0 */, 0x01, [127, 0, 0, 1], destPort, payload), 0, bnd);
                Assert.ThrowsExactly<SocketException>(() =>
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    relaySocket.Receive(ref from);
                }, "A fragmented datagram (FRAG!=0) must be dropped, so no echo comes back");
            });
        }

        // ---- harness -----------------------------------------------------------------------------------

        // Stands up two ft over temp files (`-D socksPort` on the listen side), a dual-stack UDP echo server
        // per destPort, does the SOCKS5 UDP ASSOCIATE handshake, then runs `body(control, relaySocket, bnd,...)`.
        static void RunUdpTunnel(int socksPort, int[] destPorts, Action<TcpClient, UdpClient, IPEndPoint, int[]> body)
        {
            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();

            var listenThread = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"-D {socksPort} --write ""{writeFilename}"" --read ""{readFilename}""")));
            listenThread.Start();

            var forwardThread = new Thread(() =>
                ft.Program.Main(StringUtility.CommandLineToArgs($@"--read ""{writeFilename}"" --write ""{readFilename}""")));
            forwardThread.Start();

            var echoes = destPorts.Select(StartUdpEcho).ToList();

            TcpClient? control = null;
            UdpClient? relaySocket = null;
            try
            {
                control = ConnectToSocksPort(socksPort);
                var bnd = UdpAssociate(control);

                relaySocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { Client = { ReceiveTimeout = 30000 } };
                body(control, relaySocket, bnd, destPorts);
            }
            finally
            {
                try { relaySocket?.Close(); } catch { }
                try { control?.Close(); } catch { }
                foreach (var e in echoes) { try { e.Stop = true; e.Socket.Close(); } catch { } }

                listenThread.Interrupt(); listenThread.Join();
                forwardThread.Interrupt(); forwardThread.Join();
                try { File.Delete(readFilename); } catch { }
                try { File.Delete(writeFilename); } catch { }
            }
        }

        sealed class EchoServer { public required UdpClient Socket; public volatile bool Stop; }

        static EchoServer StartUdpEcho(int port)
        {
            var socket = new UdpClient(AddressFamily.InterNetworkV6);
            socket.Client.DualMode = true;   // accept both ::1 and 127.0.0.1 (for the domain/IPv6 cases)
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            var server = new EchoServer { Socket = socket };

            new Thread(() =>
            {
                try
                {
                    while (!server.Stop)
                    {
                        var from = new IPEndPoint(IPAddress.IPv6Any, 0);
                        var data = socket.Receive(ref from);
                        socket.Send(data, data.Length, from);
                    }
                }
                catch { }
            })
            { IsBackground = true }.Start();

            return server;
        }

        // TCP handshake + UDP ASSOCIATE; returns the BND (relay) endpoint the client must send datagrams to.
        static IPEndPoint UdpAssociate(TcpClient control)
        {
            var stream = control.GetStream();
            stream.ReadTimeout = 30000;

            stream.Write([0x05, 0x01, 0x00], 0, 3);                      // greeting: no-auth
            var methodSelect = ReadExactly(stream, 2);
            Assert.AreEqual((byte)0x00, methodSelect[1], "server should select no-auth");

            stream.Write([0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0], 0, 10);   // UDP ASSOCIATE, DST 0.0.0.0:0

            var head = ReadExactly(stream, 4);                          // VER REP RSV ATYP
            Assert.AreEqual((byte)0x05, head[0]);
            Assert.AreEqual((byte)0x00, head[1], $"UDP ASSOCIATE failed (REP 0x{head[1]:x2})");
            var addrLen = head[3] == 0x04 ? 16 : 4;
            var addr = ReadExactly(stream, addrLen);
            var portBytes = ReadExactly(stream, 2);
            var bndPort = (portBytes[0] << 8) | portBytes[1];
            return new IPEndPoint(new IPAddress(addr), bndPort);
        }

        static byte[] SendAndReceive(UdpClient relaySocket, IPEndPoint bnd, byte[] requestDatagram, out int headerLen)
        {
            // The reply echoes the same ATYP+ADDR+PORT, so the reply header is the same length as ours.
            headerLen = UdpHeaderLength(requestDatagram);
            relaySocket.Send(requestDatagram, requestDatagram.Length, bnd);
            var from = new IPEndPoint(IPAddress.Any, 0);
            return relaySocket.Receive(ref from);
        }

        static int UdpHeaderLength(byte[] datagram)
        {
            var atyp = datagram[3];
            var addrLen = atyp switch { 0x01 => 4, 0x04 => 16, 0x03 => 1 + datagram[4], _ => throw new Exception("bad atyp") };
            return 4 + addrLen + 2;
        }

        static byte[] AddressBytes(byte atyp, string host) => atyp switch
        {
            0x01 => IPAddress.Parse(host).GetAddressBytes(),
            0x04 => IPAddress.Parse(host).GetAddressBytes(),
            0x03 => Encoding.ASCII.GetBytes(host),
            _ => throw new Exception("bad atyp")
        };

        static byte[] BuildRequestDatagram(byte frag, byte atyp, byte[] addr, int port, byte[] payload)
        {
            var list = new List<byte> { 0, 0, frag, atyp };
            if (atyp == 0x03) list.Add((byte)addr.Length);
            list.AddRange(addr);
            list.Add((byte)(port >> 8)); list.Add((byte)(port & 0xFF));
            list.AddRange(payload);
            return list.ToArray();
        }

        static byte[] BuildUdpDatagram(byte frag, byte atyp, byte[] addr, int port, byte[] data)
            => BuildRequestDatagram(frag, atyp, addr, port, data);

        static TcpClient ConnectToSocksPort(int socksPort)
        {
            var client = new TcpClient();
            var start = DateTime.Now;
            while (true)
            {
                if ((DateTime.Now - start).TotalSeconds > 25) throw new Exception($"Could not connect to SOCKS port {socksPort}");
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

        static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        sealed class CapturingSink : ISocksUdpSink
        {
            public readonly List<byte[]> Sent = [];
            public readonly List<string> Removed = [];
            public void SendToClient(byte[] packet) => Sent.Add(packet);
            public void Remove(string destinationKey) => Removed.Add(destinationKey);
        }

        // Read side pre-seeded, writes captured (a single MemoryStream can't interleave the two cursors).
        sealed class DuplexTestStream(byte[] toRead) : Stream
        {
            readonly MemoryStream readSide = new(toRead);
            readonly MemoryStream writeSide = new();
            public byte[] Written() => writeSide.ToArray();
            public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);
            public override int ReadByte() => readSide.ReadByte();
            public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);
            public override void WriteByte(byte value) => writeSide.WriteByte(value);
            public override void Flush() { }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
