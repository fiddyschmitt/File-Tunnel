using System;
using System.IO;
using System.Net;
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

    public sealed class SocksException(string message) : Exception(message);

    public sealed class SocksRequest(byte version, string destination)
    {
        public byte Version { get; } = version;            // 0x04 or 0x05
        public string Destination { get; } = destination;  // "tcp://host:port"
    }

    // Minimal SOCKS4 / SOCKS4A / SOCKS5 server handshake, matching the surface modern OpenSSH's dynamic
    // forwarding implements: CONNECT only, no authentication, no BIND, no UDP ASSOCIATE.
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
            if (cmd != 0x01)   // CONNECT only (reject BIND / UDP ASSOCIATE)
            {
                WriteSocks5Reply(client, 0x07);   // command not supported
                throw new SocksException($"Unsupported SOCKS5 command 0x{cmd:x2}");
            }

            var atyp = header[3];
            string host;
            switch (atyp)
            {
                case 0x01:   // IPv4
                    var v4 = new byte[4]; client.ReadExactly(v4);
                    host = new IPAddress(v4).ToString();
                    break;
                case 0x03:   // domain name (passed through verbatim; resolved on the exit side)
                    var len = ReadByteStrict(client);
                    var name = new byte[len]; client.ReadExactly(name);
                    host = Encoding.ASCII.GetString(name);
                    break;
                case 0x04:   // IPv6 (bracket so NetworkUtilities.ParseEndpoint accepts it)
                    var v6 = new byte[16]; client.ReadExactly(v6);
                    host = $"[{new IPAddress(v6)}]";
                    break;
                default:
                    WriteSocks5Reply(client, 0x08);   // address type not supported
                    throw new SocksException($"Unsupported SOCKS5 address type 0x{atyp:x2}");
            }

            var port = ReadPort(client);
            return new SocksRequest(0x05, $"tcp://{host}:{port}");
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

            return new SocksRequest(0x04, $"tcp://{host}:{port}");
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
