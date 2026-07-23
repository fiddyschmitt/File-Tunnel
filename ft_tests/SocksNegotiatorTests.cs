using ft.Socks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ft_tests
{
    // Socket-free tests for the SOCKS4/4a/5 handshake decoder. Crafted request bytes are fed through an
    // in-memory duplex stream; we assert both the parsed destination and the exact reply bytes. No ft
    // process, no sockets - pure protocol logic. [Timeout] guards against a truncation ever spinning.
    [TestClass]
    [TestCategory("Unit")]
    public class SocksNegotiatorTests
    {
        [TestMethod]
        [Timeout(15000)]
        public void Socks5_IPv4_Connect_ParsesAndReplies()
        {
            // greeting: VER=5, NMETHODS=1, METHOD=no-auth ; request: CONNECT IPv4 1.2.3.4:8080 (0x1F90)
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x01, 0x00, 0x01, 1, 2, 3, 4, 0x1F, 0x90 }));

            var request = SocksNegotiator.Read(stream);

            Assert.AreEqual((byte)0x05, request.Version);
            Assert.AreEqual("tcp://1.2.3.4:8080", request.Destination);

            // Read wrote only the method-selection so far.
            CollectionAssert.AreEqual(new byte[] { 0x05, 0x00 }, stream.Written());

            SocksNegotiator.WriteReply(stream, request.Version, (byte)ConnectStatus.Success);

            CollectionAssert.AreEqual(
                new byte[] { 0x05, 0x00,                                      // method-select
                             0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },      // CONNECT success, BND 0.0.0.0:0
                stream.Written());
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks5_Domain_PassesHostnameThrough()
        {
            var host = Encoding.ASCII.GetBytes("example.com");
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x01, 0x00, 0x03, (byte)host.Length },
                host,
                new byte[] { 0x01, 0xBB }));   // port 443

            var request = SocksNegotiator.Read(stream);

            Assert.AreEqual("tcp://example.com:443", request.Destination);
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks5_IPv6_IsBracketed()
        {
            var ipv6 = new byte[16]; ipv6[15] = 1;   // ::1
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x01, 0x00, 0x04 },
                ipv6,
                new byte[] { 0x00, 0x50 }));   // port 80

            var request = SocksNegotiator.Read(stream);

            Assert.AreEqual("tcp://[::1]:80", request.Destination);
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks5_NoAcceptableMethods_RejectsWith05FF()
        {
            var stream = new DuplexTestStream(new byte[] { 0x05, 0x01, 0x02 });   // offers only GSSAPI (0x02)

            Assert.ThrowsExactly<SocksException>(() => SocksNegotiator.Read(stream));
            CollectionAssert.AreEqual(new byte[] { 0x05, 0xFF }, stream.Written());
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks5_BindCommand_RejectedWithRep07()
        {
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x05, 0x01, 0x00 },
                new byte[] { 0x05, 0x02, 0x00, 0x01, 1, 2, 3, 4, 0x00, 0x50 }));   // CMD=02 BIND

            Assert.ThrowsExactly<SocksException>(() => SocksNegotiator.Read(stream));

            var written = stream.Written();
            CollectionAssert.AreEqual(
                new byte[] { 0x05, 0x00,                                     // method-select
                             0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 },     // REP=0x07 command not supported
                written);
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks4_Connect_ParsesAndGrants()
        {
            // VN=4, CD=CONNECT, port 0x1234 (big-endian), IP 1.2.3.4, empty USERID
            var stream = new DuplexTestStream(new byte[] { 0x04, 0x01, 0x12, 0x34, 1, 2, 3, 4, 0x00 });

            var request = SocksNegotiator.Read(stream);

            Assert.AreEqual((byte)0x04, request.Version);
            Assert.AreEqual("tcp://1.2.3.4:4660", request.Destination);   // 0x1234 == 4660 proves big-endian
            Assert.AreEqual(0, stream.Written().Length, "SOCKS4 has no greeting, so Read writes nothing");

            SocksNegotiator.WriteReply(stream, request.Version, (byte)ConnectStatus.Success);

            CollectionAssert.AreEqual(new byte[] { 0x00, 0x5A, 0, 0, 0, 0, 0, 0 }, stream.Written());
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks4a_HostnameFollowsUserId()
        {
            var host = Encoding.ASCII.GetBytes("example.com");
            var stream = new DuplexTestStream(Concat(
                new byte[] { 0x04, 0x01, 0x00, 0x50, 0, 0, 0, 1, 0x00 },   // IP 0.0.0.1 => SOCKS4A, empty USERID
                host,
                new byte[] { 0x00 }));

            var request = SocksNegotiator.Read(stream);

            Assert.AreEqual("tcp://example.com:80", request.Destination);
        }

        [TestMethod]
        [Timeout(15000)]
        public void Socks4_BindCommand_RejectedWith5B()
        {
            var stream = new DuplexTestStream(new byte[] { 0x04, 0x02, 0x00, 0x50, 1, 2, 3, 4, 0x00 });   // CD=02

            Assert.ThrowsExactly<SocksException>(() => SocksNegotiator.Read(stream));
            CollectionAssert.AreEqual(new byte[] { 0x00, 0x5B, 0, 0, 0, 0, 0, 0 }, stream.Written());
        }

        [TestMethod]
        [Timeout(15000)]
        public void UnsupportedVersion_Throws()
        {
            var stream = new DuplexTestStream(new byte[] { 0x06, 0x00 });
            Assert.ThrowsExactly<SocksException>(() => SocksNegotiator.Read(stream));
        }

        [TestMethod]
        [Timeout(15000)]   // if a truncated handshake ever spins instead of throwing, this fails on the timeout
        public void TruncatedRequest_ThrowsEndOfStream_DoesNotHang()
        {
            // VER=5, NMETHODS=1 but the method byte is missing.
            var stream = new DuplexTestStream(new byte[] { 0x05, 0x01 });
            Assert.ThrowsExactly<EndOfStreamException>(() => SocksNegotiator.Read(stream));
        }

        static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        // Read side is pre-seeded; writes are captured. A single MemoryStream can't cleanly interleave a
        // pre-loaded read cursor with a captured write cursor, hence the split.
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
