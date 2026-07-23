using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ft.Socks
{
    // The dial outcome the exit side reports back so the SOCKS host can emit an accurate reply.
    public enum ConnectStatus : byte
    {
        Success = 0,
        GeneralFailure = 1,
        ConnectionRefused = 2,
        HostUnreachable = 3,
        TtlExpired = 4
    }

    // The SOCKS5 request command we act on. CONNECT (TCP) and UDP ASSOCIATE; BIND is rejected.
    public enum SocksCommand
    {
        Connect,
        UdpAssociate
    }

    public sealed class SocksException(string message) : Exception(message);

    public sealed class SocksRequest(byte version, SocksCommand command, string destination, IPEndPoint? udpClientDeclaredEndpoint = null)
    {
        public byte Version { get; } = version;                                    // 0x04 or 0x05
        public SocksCommand Command { get; } = command;
        public string Destination { get; } = destination;                          // "tcp://host:port" for Connect; "" for UdpAssociate
        public IPEndPoint? UdpClientDeclaredEndpoint { get; } = udpClientDeclaredEndpoint;   // client's declared UDP source (usually 0.0.0.0:0 → null)
    }

    // A parsed SOCKS5 UDP request datagram (RFC 1928 §7): RSV FRAG ATYP DST.ADDR DST.PORT DATA.
    public sealed class ParsedUdpDatagram(byte frag, string destinationString, byte[] replyHeaderPrefix, byte[] datagram, int dataOffset, int dataLength)
    {
        public byte Frag { get; } = frag;
        public string DestinationString { get; } = destinationString;   // "udp://host:port"
        public byte[] ReplyHeaderPrefix { get; } = replyHeaderPrefix;    // 00 00 00 + ATYP+ADDR+PORT, echoed on replies

        public byte[] ExtractData()
        {
            var data = new byte[dataLength];
            Buffer.BlockCopy(datagram, dataOffset, data, 0, dataLength);
            return data;
        }
    }

    // Minimal SOCKS4 / SOCKS4A / SOCKS5 server handshake plus the SOCKS5 UDP-associate datagram codec,
    // matching the surface modern implementations expose: CONNECT + UDP ASSOCIATE, no authentication, no
    // BIND (SOCKS4/4a stay CONNECT-only - UDP is a SOCKS5 feature).
    //
    // Operates directly on the client Stream. NB: never wrap it in a BinaryReader - that read-aheads and
    // would swallow the client's first application bytes. Read exactly what's needed; multi-byte fields
    // (ports) are big-endian; truncated input throws EndOfStreamException (never spins).
    public static class SocksNegotiator
    {
        public static SocksRequest Read(Stream client)
        {
            var version = ReadByteStrict(client);
            return version switch
            {
                0x05 => ReadSocks5(client),
                0x04 => ReadSocks4(client),
                _ => throw new SocksException($"Unsupported SOCKS version 0x{version:x2}")
            };
        }

        static SocksRequest ReadSocks5(Stream client)
        {
            // greeting: NMETHODS, METHODS[NMETHODS]
            var nMethods = ReadByteStrict(client);
            var methods = new byte[nMethods];
            client.ReadExactly(methods);

            if (Array.IndexOf(methods, (byte)0x00) < 0)   // require the "no authentication" method
            {
                client.Write([0x05, 0xFF]); client.Flush();
                throw new SocksException("SOCKS5 client did not offer the no-auth method");
            }
            client.Write([0x05, 0x00]); client.Flush();   // select no-auth

            // request: VER, CMD, RSV, ATYP
            var header = new byte[4];
            client.ReadExactly(header);
            if (header[0] != 0x05) throw new SocksException("Bad SOCKS5 request version");

            var cmd = header[1];
            if (cmd != 0x01 && cmd != 0x03)   // CONNECT + UDP ASSOCIATE only (reject BIND)
            {
                WriteSocks5Reply(client, 0x07);   // command not supported
                throw new SocksException($"Unsupported SOCKS5 command 0x{cmd:x2}");
            }

            var atyp = header[3];
            var (host, addr) = ReadAddress(client, atyp);   // may WriteSocks5Reply(0x08)+throw on bad ATYP
            var port = ReadPort(client);

            if (cmd == 0x01)
            {
                return new SocksRequest(0x05, SocksCommand.Connect, $"tcp://{host}:{port}");
            }

            // UDP ASSOCIATE: DST is the address the client will send datagrams FROM (usually 0.0.0.0:0).
            return new SocksRequest(0x05, SocksCommand.UdpAssociate, "", TryBuildDeclaredEndpoint(atyp, addr, port));
        }

        static SocksRequest ReadSocks4(Stream client)
        {
            // request: VN(=04, already consumed), CD, DSTPORT(2), DSTIP(4), USERID, 0x00
            var cd = ReadByteStrict(client);
            var port = ReadPort(client);
            var ip = new byte[4]; client.ReadExactly(ip);
            ReadNullTerminated(client);   // USERID - ignored (no auth)

            if (cd != 0x01)   // CONNECT only
            {
                WriteSocks4Reply(client, 0x5B);   // rejected
                throw new SocksException($"Unsupported SOCKS4 command 0x{cd:x2}");
            }

            // SOCKS4A: DSTIP 0.0.0.x (x != 0) means a null-terminated hostname follows the USERID
            string host;
            if (ip[0] == 0 && ip[1] == 0 && ip[2] == 0 && ip[3] != 0)
            {
                host = ReadNullTerminated(client);
            }
            else
            {
                host = new IPAddress(ip).ToString();
            }

            return new SocksRequest(0x04, SocksCommand.Connect, $"tcp://{host}:{port}");
        }

        // Reads a SOCKS5 ATYP-tagged address from the stream, returning the rendered host and the raw
        // address bytes. Shared by CONNECT and UDP ASSOCIATE request parsing.
        static (string Host, byte[] Addr) ReadAddress(Stream client, byte atyp)
        {
            switch (atyp)
            {
                case 0x01:   // IPv4
                    var v4 = new byte[4]; client.ReadExactly(v4);
                    return (FormatHost(0x01, v4), v4);
                case 0x03:   // domain name (passed through verbatim; resolved on the exit side)
                    var len = ReadByteStrict(client);
                    var name = new byte[len]; client.ReadExactly(name);
                    return (FormatHost(0x03, name), name);
                case 0x04:   // IPv6
                    var v6 = new byte[16]; client.ReadExactly(v6);
                    return (FormatHost(0x04, v6), v6);
                default:
                    WriteSocks5Reply(client, 0x08);   // address type not supported
                    throw new SocksException($"Unsupported SOCKS5 address type 0x{atyp:x2}");
            }
        }

        // Renders an ATYP-tagged address to the "host" part of a proto://host:port string (IPv6 bracketed
        // so NetworkUtilities.ParseEndpoint accepts it). Shared by the request and datagram parsers.
        static string FormatHost(byte atyp, byte[] addr) => atyp switch
        {
            0x01 => new IPAddress(addr).ToString(),
            0x03 => Encoding.ASCII.GetString(addr),
            0x04 => $"[{new IPAddress(addr)}]",
            _ => throw new SocksException($"Unsupported address type 0x{atyp:x2}")
        };

        static IPEndPoint? TryBuildDeclaredEndpoint(byte atyp, byte[] addr, int port)
        {
            // Only concrete IP literals are useful as a source filter; a domain or 0.0.0.0:0 → null, meaning
            // "learn the client's UDP source from its first datagram".
            if ((atyp == 0x01 || atyp == 0x04) && port != 0)
            {
                var ip = new IPAddress(addr);
                if (!ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                {
                    return new IPEndPoint(ip, port);
                }
            }
            return null;
        }

        // Parses a SOCKS5 UDP request datagram. Returns false (drop the datagram) on truncation/bad ATYP.
        // FRAG is surfaced but not acted on here - the caller drops FRAG != 0 (fragmentation unsupported).
        public static bool TryParseUdpDatagram(byte[] datagram, out ParsedUdpDatagram result)
        {
            result = null!;

            if (datagram.Length < 4) return false;   // RSV(2) + FRAG(1) + ATYP(1)

            var frag = datagram[2];
            var atyp = datagram[3];

            int addrLen;
            switch (atyp)
            {
                case 0x01: addrLen = 4; break;
                case 0x04: addrLen = 16; break;
                case 0x03:
                    if (datagram.Length < 5) return false;
                    addrLen = 1 + datagram[4];   // length byte + N
                    break;
                default: return false;
            }

            var headerLen = 4 + addrLen + 2;   // RSV+FRAG+ATYP + ADDR(+len) + PORT
            if (datagram.Length < headerLen) return false;

            string host;
            int port;
            if (atyp == 0x03)
            {
                var nameLen = datagram[4];
                var name = new byte[nameLen];
                Buffer.BlockCopy(datagram, 5, name, 0, nameLen);
                host = FormatHost(0x03, name);
                port = (datagram[5 + nameLen] << 8) | datagram[5 + nameLen + 1];
            }
            else
            {
                var addr = new byte[addrLen];
                Buffer.BlockCopy(datagram, 4, addr, 0, addrLen);
                host = FormatHost(atyp, addr);
                port = (datagram[4 + addrLen] << 8) | datagram[4 + addrLen + 1];
            }

            // Reply prefix = RSV(00 00) FRAG(00) + the inbound ATYP+ADDR+PORT bytes verbatim.
            var atypThroughPort = headerLen - 3;
            var replyPrefix = new byte[3 + atypThroughPort];
            Buffer.BlockCopy(datagram, 3, replyPrefix, 3, atypThroughPort);

            result = new ParsedUdpDatagram(frag, $"udp://{host}:{port}", replyPrefix, datagram, headerLen, datagram.Length - headerLen);
            return true;
        }

        // Writes the final CONNECT reply carrying the real dial result (a ConnectStatus byte).
        public static void WriteReply(Stream client, byte version, byte status)
        {
            if (version == 0x05)
            {
                byte rep = (ConnectStatus)status switch
                {
                    ConnectStatus.Success => 0x00,
                    ConnectStatus.ConnectionRefused => 0x05,
                    ConnectStatus.HostUnreachable => 0x04,
                    ConnectStatus.TtlExpired => 0x06,
                    _ => 0x01
                };
                WriteSocks5Reply(client, rep);
            }
            else   // SOCKS4 has only granted (0x5A) / rejected (0x5B)
            {
                WriteSocks4Reply(client, (ConnectStatus)status == ConnectStatus.Success ? (byte)0x5A : (byte)0x5B);
            }
        }

        // Writes the SOCKS5 UDP ASSOCIATE reply. Unlike WriteSocks5Reply this carries a real BND.ADDR:PORT
        // (the relay socket the client must send datagrams to), with ATYP matching the address family.
        public static void WriteSocks5UdpReply(Stream client, byte rep, IPEndPoint bnd)
        {
            var addr = bnd.Address.GetAddressBytes();
            var atyp = bnd.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01;

            var reply = new byte[4 + addr.Length + 2];
            reply[0] = 0x05;
            reply[1] = rep;
            reply[2] = 0x00;
            reply[3] = atyp;
            Buffer.BlockCopy(addr, 0, reply, 4, addr.Length);
            reply[4 + addr.Length] = (byte)(bnd.Port >> 8);
            reply[4 + addr.Length + 1] = (byte)(bnd.Port & 0xFF);

            client.Write(reply, 0, reply.Length);
            client.Flush();
        }

        // VER, REP, RSV, ATYP=IPv4, BND.ADDR=0.0.0.0, BND.PORT=0 (BND is unused by CONNECT clients).
        static void WriteSocks5Reply(Stream client, byte rep)
        {
            client.Write([0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0]);
            client.Flush();
        }

        // Reply version byte is 0x00 (not 0x04); CD; DSTPORT(2)=0; DSTIP(4)=0.
        static void WriteSocks4Reply(Stream client, byte cd)
        {
            client.Write([0x00, cd, 0, 0, 0, 0, 0, 0]);
            client.Flush();
        }

        static int ReadPort(Stream client)
        {
            var buf = new byte[2]; client.ReadExactly(buf);
            return (buf[0] << 8) | buf[1];   // big-endian
        }

        static byte ReadByteStrict(Stream client)
        {
            var b = client.ReadByte();
            if (b < 0) throw new EndOfStreamException("SOCKS handshake truncated");
            return (byte)b;
        }

        static string ReadNullTerminated(Stream client)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = client.ReadByte();
                if (b < 0) throw new EndOfStreamException("SOCKS handshake truncated");
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
