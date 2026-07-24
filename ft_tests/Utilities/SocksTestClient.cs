using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ft_tests.Utilities
{
    // Minimal SOCKS5 UDP-ASSOCIATE client for the cross-OS SOCKS end-to-end test. No common CLI implements
    // SOCKS5 UDP (curl/PowerShell/dig/nc/socat are TCP-only for SOCKS), so the UDP leg is driven from the
    // harness while TCP is exercised with real curl on the node. Each method does the full associate
    // handshake over TCP, then one datagram round-trip through the returned relay socket.
    public static class SocksTestClient
    {
        // UDP ASSOCIATE + a DNS A-query for `domain` to `resolverIp:53`; asserts a valid response
        // (matching transaction id, RCODE 0, >= 1 answer record).
        public static void AssertUdpDnsResolves(IPEndPoint proxy, string resolverIp, string domain, int timeoutMs = 20000)
        {
            var (control, bnd) = UdpAssociate(proxy, timeoutMs);
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { Client = { ReceiveTimeout = timeoutMs } };
                const ushort txid = 0x4a4b;
                udp.Send(Wrap(0x01, IPAddress.Parse(resolverIp).GetAddressBytes(), 53, BuildDnsQuery(txid, domain)), bnd);

                var from = new IPEndPoint(IPAddress.Any, 0);
                var reply = Unwrap(udp.Receive(ref from));

                Assert.IsTrue(reply.Length >= 12, "DNS response too short");
                Assert.AreEqual(txid, (ushort)((reply[0] << 8) | reply[1]), "DNS transaction id mismatch");
                Assert.AreEqual(0, reply[3] & 0x0F, "DNS RCODE non-zero");
                var ancount = (reply[6] << 8) | reply[7];
                Assert.IsTrue(ancount >= 1, $"DNS returned no answers (ANCOUNT={ancount})");
            }
            finally
            {
                try { control.Close(); } catch { }
            }
        }

        // UDP ASSOCIATE + a single echo round-trip to `echoIp:echoPort`; asserts the bytes match exactly.
        public static void AssertUdpEcho(IPEndPoint proxy, string echoIp, int echoPort, byte[] payload, int timeoutMs = 20000)
        {
            var (control, bnd) = UdpAssociate(proxy, timeoutMs);
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { Client = { ReceiveTimeout = timeoutMs } };
                udp.Send(Wrap(0x01, IPAddress.Parse(echoIp).GetAddressBytes(), echoPort, payload), bnd);

                var from = new IPEndPoint(IPAddress.Any, 0);
                var got = Unwrap(udp.Receive(ref from));

                Assert.IsTrue(payload.SequenceEqual(got), $"UDP echo mismatch: sent {payload.Length} bytes, got {got.Length}");
            }
            finally
            {
                try { control.Close(); } catch { }
            }
        }

        static (TcpClient Control, IPEndPoint Bnd) UdpAssociate(IPEndPoint proxy, int timeoutMs)
        {
            var control = new TcpClient();
            var start = DateTime.Now;
            while (true)
            {
                try { control.Connect(proxy); break; }
                catch
                {
                    if ((DateTime.Now - start).TotalMilliseconds > timeoutMs) throw new Exception($"Could not connect to SOCKS proxy {proxy}");
                    Thread.Sleep(200);
                }
            }

            var stream = control.GetStream();
            stream.ReadTimeout = timeoutMs;

            stream.Write([0x05, 0x01, 0x00], 0, 3);                            // greeting: no-auth
            var methodSelect = ReadExactly(stream, 2);
            if (methodSelect[1] != 0x00) throw new Exception("SOCKS5 server did not select no-auth");

            stream.Write([0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0], 0, 10);   // UDP ASSOCIATE, DST 0.0.0.0:0

            var head = ReadExactly(stream, 4);                                 // VER REP RSV ATYP
            if (head[1] != 0x00) throw new Exception($"UDP ASSOCIATE failed (REP 0x{head[1]:x2})");
            var addr = ReadExactly(stream, head[3] == 0x04 ? 16 : 4);
            var portBytes = ReadExactly(stream, 2);

            return (control, new IPEndPoint(new IPAddress(addr), (portBytes[0] << 8) | portBytes[1]));
        }

        static byte[] Wrap(byte atyp, byte[] addr, int port, byte[] payload)
        {
            var list = new List<byte> { 0, 0, 0, atyp };   // RSV RSV FRAG ATYP
            if (atyp == 0x03) list.Add((byte)addr.Length);
            list.AddRange(addr);
            list.Add((byte)(port >> 8)); list.Add((byte)(port & 0xFF));
            list.AddRange(payload);
            return list.ToArray();
        }

        static byte[] Unwrap(byte[] datagram)
        {
            var atyp = datagram[3];
            var headerLen = atyp switch { 0x01 => 10, 0x04 => 22, 0x03 => 4 + 1 + datagram[4] + 2, _ => throw new Exception("bad ATYP in reply") };
            return datagram.Skip(headerLen).ToArray();
        }

        static byte[] BuildDnsQuery(ushort txid, string domain)
        {
            var q = new List<byte>
            {
                (byte)(txid >> 8), (byte)(txid & 0xFF),
                0x01, 0x00,               // standard query, recursion desired
                0x00, 0x01,               // QDCOUNT=1
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            foreach (var label in domain.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                q.Add((byte)bytes.Length);
                q.AddRange(bytes);
            }
            q.Add(0x00);                  // end of QNAME
            q.AddRange([0x00, 0x01]);     // QTYPE=A
            q.AddRange([0x00, 0x01]);     // QCLASS=IN
            return q.ToArray();
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
