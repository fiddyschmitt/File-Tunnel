using ft.Commands;
using ft.Streams;
using System;
using System.IO;
using System.Text;

namespace ft_tests
{
    /// <summary>
    /// Hermetic tests for Forward's (de)serialization, focused on the length/EOF guard. A torn or hostile
    /// payload length must fail fast — EndOfStreamException on truncation (which routes to the pump's
    /// resend/retry path) and InvalidDataException on an out-of-range length — rather than spin at 100% CPU
    /// on read==0 or attempt a multi-GB allocation from a raw int32 read straight out of the file. The
    /// happy-path round-trip is asserted too, so the guard can't silently corrupt normal Forwards.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class ForwardTests
    {
        [TestMethod]
        [Timeout(15000)]
        public void Forward_RoundTrips_Intact()
        {
            var payload = new byte[512];
            new Random(1234).NextBytes(payload);

            var bytes = Serialize(new Forward(connectionId: 99, payload));

            var forward = Deserialize(bytes) as Forward;

            Assert.IsNotNull(forward, "Expected a Forward back");
            Assert.AreEqual(99, forward!.ConnectionId);
            CollectionAssert.AreEqual(payload, forward.Payload, "Payload did not survive the round-trip byte-for-byte");
        }

        [TestMethod]
        [Timeout(15000)]   // if the read==0 spin ever regresses this hangs and fails on the timeout, not silently
        public void Forward_TruncatedPayload_ThrowsEndOfStream_DoesNotHang()
        {
            var payload = new byte[512];
            new Random(4321).NextBytes(payload);

            var full = Serialize(new Forward(connectionId: 7, payload));

            // Drop the CRC (4 bytes) plus half the payload, so EOF lands INSIDE the payload read loop.
            var truncated = full[..(full.Length - 4 - 256)];

            Assert.ThrowsExactly<EndOfStreamException>(
                () => Deserialize(truncated),
                "A truncated payload must throw EndOfStreamException (routing to the resend path), not spin on read==0.");
        }

        [DataTestMethod]
        [DataRow(int.MaxValue)]                     // ~2GB allocation demand
        [DataRow(Forward.MAX_PAYLOAD_LENGTH + 1)]   // just past the cap
        [DataRow(-1)]                               // negative
        [Timeout(15000)]
        public void Forward_OutOfRangeLength_ThrowsInvalidData_BeforeAllocating(int hostileLength)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Forward.COMMAND_ID);   // command id -> Forward
                writer.Write((ulong)0);             // packet number
                writer.Write(42);                   // ConnectionId
                writer.Write(hostileLength);        // payload length (hostile / corrupt)
            }

            Assert.ThrowsExactly<InvalidDataException>(
                () => Deserialize(ms.ToArray()),
                $"A payload length of {hostileLength} must be rejected before allocating.");
        }

        private static byte[] Serialize(Forward forward)
        {
            using var ms = new MemoryStream();
            using (var hashing = new HashingStream(ms, verbose: false, tunnelTimeoutMilliseconds: 10000))
            using (var writer = new BinaryWriter(hashing))
            {
                forward.Serialise(writer);
                writer.Flush();
            }
            return ms.ToArray();
        }

        private static Command? Deserialize(byte[] bytes)
        {
            var ms = new MemoryStream(bytes);
            using var hashing = new HashingStream(ms, verbose: false, tunnelTimeoutMilliseconds: 10000);
            using var reader = new BinaryReader(hashing);
            return Command.Deserialise(reader);
        }
    }
}
