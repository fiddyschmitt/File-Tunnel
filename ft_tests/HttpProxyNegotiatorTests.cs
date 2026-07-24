using ft.Http;
using ft.Socks;
using System;
using System.IO;
using System.Text;

namespace ft_tests
{
    // Socket-free tests for the HTTP CONNECT proxy handshake decoder: crafted request bytes through an
    // in-memory duplex stream, asserting the parsed destination and the exact reply bytes. [Timeout] guards
    // against a truncated request ever spinning.
    [TestClass]
    [TestCategory("Unit")]
    public class HttpProxyNegotiatorTests
    {
        [TestMethod]
        [Timeout(15000)]
        public void Connect_ParsesDestination_WritesNothingYet()
        {
            var stream = new DuplexTestStream(Bytes("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\nProxy-Connection: keep-alive\r\n\r\n"));

            var request = HttpProxyNegotiator.Read(stream);

            Assert.AreEqual("tcp://example.com:443", request.Destination);
            Assert.AreEqual(0, stream.Written().Length, "Read must not write anything - the reply is deferred until the dial result");
        }

        [TestMethod]
        [Timeout(15000)]
        public void Connect_NoHeaders_Works()
        {
            var stream = new DuplexTestStream(Bytes("CONNECT 1.2.3.4:8080 HTTP/1.1\r\n\r\n"));
            Assert.AreEqual("tcp://1.2.3.4:8080", HttpProxyNegotiator.Read(stream).Destination);
        }

        [TestMethod]
        [Timeout(15000)]
        public void Connect_IPv6_PassesBracketedAuthority()
        {
            var stream = new DuplexTestStream(Bytes("CONNECT [::1]:443 HTTP/1.1\r\n\r\n"));
            Assert.AreEqual("tcp://[::1]:443", HttpProxyNegotiator.Read(stream).Destination);
        }

        [TestMethod]
        [Timeout(15000)]
        public void WriteReply_Success_Is200()
        {
            var stream = new DuplexTestStream([]);
            HttpProxyNegotiator.WriteReply(stream, (byte)ConnectStatus.Success);
            Assert.AreEqual("HTTP/1.1 200 Connection Established\r\n\r\n", Str(stream.Written()));
        }

        [TestMethod]
        [Timeout(15000)]
        public void WriteReply_Refused_Is502()
        {
            var stream = new DuplexTestStream([]);
            HttpProxyNegotiator.WriteReply(stream, (byte)ConnectStatus.ConnectionRefused);
            Assert.AreEqual("HTTP/1.1 502 Bad Gateway\r\n\r\n", Str(stream.Written()));
        }

        [TestMethod]
        [Timeout(15000)]
        public void WriteReply_Timeout_Is504()
        {
            var stream = new DuplexTestStream([]);
            HttpProxyNegotiator.WriteReply(stream, (byte)ConnectStatus.TtlExpired);
            Assert.AreEqual("HTTP/1.1 504 Gateway Timeout\r\n\r\n", Str(stream.Written()));
        }

        [TestMethod]
        [Timeout(15000)]
        public void NonConnectMethod_Rejected405()
        {
            var stream = new DuplexTestStream(Bytes("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n"));
            Assert.ThrowsExactly<HttpProxyException>(() => HttpProxyNegotiator.Read(stream));
            Assert.IsTrue(Str(stream.Written()).StartsWith("HTTP/1.1 405"), Str(stream.Written()));
        }

        [TestMethod]
        [Timeout(15000)]
        public void MissingPort_Rejected400()
        {
            var stream = new DuplexTestStream(Bytes("CONNECT example.com HTTP/1.1\r\n\r\n"));
            Assert.ThrowsExactly<HttpProxyException>(() => HttpProxyNegotiator.Read(stream));
            Assert.IsTrue(Str(stream.Written()).StartsWith("HTTP/1.1 400"), Str(stream.Written()));
        }

        [TestMethod]
        [Timeout(15000)]   // if a truncated request ever spins instead of throwing, this fails on the timeout
        public void Truncated_ThrowsEndOfStream_DoesNotHang()
        {
            var stream = new DuplexTestStream(Bytes("CONNECT example.com:443 HTTP/1.1\r\n"));   // no blank-line terminator
            Assert.ThrowsExactly<EndOfStreamException>(() => HttpProxyNegotiator.Read(stream));
        }

        static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);
        static string Str(byte[] b) => Encoding.ASCII.GetString(b);

        // Read side pre-seeded, writes captured (same helper as SocksNegotiatorTests).
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
