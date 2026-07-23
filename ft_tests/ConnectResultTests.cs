using ft.Commands;
using ft.Streams;
using System.IO;
using System.Text;

namespace ft_tests
{
    // Serialization guard for the ConnectResult command (SOCKS accurate-reply ack): a happy-path round-trip
    // plus a truncation check, matching ForwardTests' ethos - a torn command must throw (routing to the
    // pump's resend path), never spin or mis-read.
    [TestClass]
    [TestCategory("Unit")]
    public class ConnectResultTests
    {
        [TestMethod]
        [Timeout(15000)]
        public void ConnectResult_RoundTrips_Intact()
        {
            var bytes = Serialize(new ConnectResult(connectionId: 4242, status: 3));

            var result = Deserialize(bytes) as ConnectResult;

            Assert.IsNotNull(result, "Expected a ConnectResult back");
            Assert.AreEqual(4242, result!.ConnectionId);
            Assert.AreEqual((byte)3, result.Status);
        }

        [TestMethod]
        [Timeout(15000)]
        public void ConnectResult_Truncated_ThrowsEndOfStream()
        {
            var full = Serialize(new ConnectResult(connectionId: 7, status: 2));

            // Drop the CRC (4 bytes) and the Status byte, so EOF lands on the Status read.
            var truncated = full[..(full.Length - 5)];

            Assert.ThrowsExactly<EndOfStreamException>(() => Deserialize(truncated));
        }

        static byte[] Serialize(ConnectResult command)
        {
            using var ms = new MemoryStream();
            using (var hashing = new HashingStream(ms, verbose: false, tunnelTimeoutMilliseconds: 10000))
            using (var writer = new BinaryWriter(hashing))
            {
                command.Serialise(writer);
                writer.Flush();
            }
            return ms.ToArray();
        }

        static Command? Deserialize(byte[] bytes)
        {
            var ms = new MemoryStream(bytes);
            using var hashing = new HashingStream(ms, verbose: false, tunnelTimeoutMilliseconds: 10000);
            using var reader = new BinaryReader(hashing);
            return Command.Deserialise(reader);
        }
    }
}
