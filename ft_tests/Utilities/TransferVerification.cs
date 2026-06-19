using System.Linq;
using System.Net.Sockets;

namespace ft_tests.Utilities
{
    /// <summary>
    /// Shared transfer-and-verify primitive used by BOTH the in-process TCP tests
    /// (<see cref="TcpUnitTests"/>) and the cross-machine end-to-end tests (<see cref="EndToEndTests"/>).
    ///
    /// Previously each of those files had its own copy of this logic and they had quietly drifted
    /// apart: the e2e copy never asserted (it returned a bool that the caller discarded, so silent
    /// data corruption passed), while the TCP copy looped forever if the peer closed early. This
    /// single implementation closes both gaps — it always asserts byte-for-byte equality, and it
    /// breaks on a zero-length read so a half-closed stream can't hang the test.
    /// </summary>
    public static class TransferVerification
    {
        /// <summary>
        /// Writes <paramref name="toSend"/> to <paramref name="sender"/>, reads the same number of
        /// bytes back from <paramref name="receiver"/>, and asserts they are identical. Throws
        /// (failing the test) on truncation or content mismatch.
        /// </summary>
        public static void TestDirection(string direction, TcpClient sender, TcpClient receiver, byte[] toSend)
        {
            sender.GetStream().Write(toSend, 0, toSend.Length);

            var received = new byte[toSend.Length];

            int totalRead = 0;
            while (totalRead < toSend.Length)
            {
                var toRead = Math.Min(1024 * 1024, received.Length - totalRead);
                var read = receiver.GetStream().Read(received, totalRead, toRead);

                if (read == 0)
                {
                    break; // peer closed early — stop instead of spinning forever; the assert below fails on the short read
                }

                totalRead += read;
            }

            var lengthOk = totalRead == toSend.Length;
            var contentOk = lengthOk && received.SequenceEqual(toSend);

            Assert.IsTrue(
                contentOk,
                $"[{direction}] Received {totalRead:N0}/{toSend.Length:N0} bytes; content {(lengthOk ? "differs from" : "truncated vs")} sent buffer");
        }
    }
}
