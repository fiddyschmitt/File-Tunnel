using ft.IO;
using ft.Utilities;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace ft_tests
{
    /// <summary>
    /// Fast, hermetic unit tests for ft's core primitives — no lab, no network peers, no published
    /// binaries. These cover logic that previously was only ever exercised indirectly through the
    /// full end-to-end suite: the ToggleReader/ToggleWriter purge-handshake primitive (the byte-flag
    /// signalling at the heart of the SMB/IsolatedReads work) and the endpoint parser's error paths.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class CoreUnitTests
    {
        // ---- ToggleWriter / ToggleReader: the 1-byte flag handshake primitive ----

        [TestMethod]
        [Timeout(15000)]
        public void ToggleWriter_Set_Then_ToggleReader_Observes_Value()
        {
            const long position = 8;
            const byte value = 65; // must be 1..127 so BinaryReader.PeekChar round-trips it as ASCII
            var path = Path.GetTempFileName();
            try
            {
                WriteToggle(path, position, value);

                using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(readStream);
                var toggleReader = new ToggleReader(reader, position, tunnelTimeoutMilliseconds: 10000, verbose: false);

                // Value is already present, so Wait must return promptly. Guard with a timeout so a
                // regression that breaks the read path fails the test instead of hanging it.
                var wait = Task.Run(() => toggleReader.Wait(value));
                Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(5)), "ToggleReader.Wait did not observe an already-written value within 5s");
            }
            finally
            {
                TryDelete(path);
            }
        }

        [TestMethod]
        [Timeout(15000)]
        public void ToggleReader_Wait_Blocks_Until_Value_Written()
        {
            const long position = 16;
            const byte target = 77;
            var path = Path.GetTempFileName();
            try
            {
                WriteToggle(path, position, 0); // start with a different value at the flag position

                using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(readStream);
                var toggleReader = new ToggleReader(reader, position, tunnelTimeoutMilliseconds: 10000, verbose: false);

                var wait = Task.Run(() => toggleReader.Wait(target));

                Assert.IsFalse(wait.Wait(TimeSpan.FromMilliseconds(500)), "ToggleReader.Wait returned before the target value was written");

                WriteToggle(path, position, target);

                Assert.IsTrue(wait.Wait(TimeSpan.FromSeconds(5)), "ToggleReader.Wait did not observe the value after it was written");
            }
            finally
            {
                TryDelete(path);
            }
        }

        private static void WriteToggle(string path, long position, byte value)
        {
            using var writeStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (writeStream.Length < position + 1)
            {
                writeStream.SetLength(position + 1);
            }
            using var writer = new BinaryWriter(writeStream);
            new ToggleWriter(writer, position, tunnelTimeoutMilliseconds: 10000, verbose: false).Set(value);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        // ---- NetworkUtilities.ParseEndpoint: the testable parser (it throws on bad input) ----

        [DataTestMethod]
        [DataRow("127.0.0.1:5000", "127.0.0.1", 5000)]
        [DataRow("0.0.0.0:6000", "0.0.0.0", 6000)]
        [DataRow("[::1]:7000", "::1", 7000)]
        public void ParseEndpoint_ValidIp_ReturnsIpEndpoint(string input, string expectedAddress, int expectedPort)
        {
            var endpoint = NetworkUtilities.ParseEndpoint(input);

            var ip = endpoint as IPEndPoint;
            Assert.IsNotNull(ip, $"Expected an IPEndPoint for '{input}'");
            Assert.AreEqual(IPAddress.Parse(expectedAddress), ip!.Address);
            Assert.AreEqual(expectedPort, ip.Port);
        }

        [DataTestMethod]
        [DataRow("server01:3389", "server01", 3389)]
        [DataRow("example.com:80", "example.com", 80)]
        public void ParseEndpoint_Hostname_ReturnsDnsEndpoint(string input, string expectedHost, int expectedPort)
        {
            var endpoint = NetworkUtilities.ParseEndpoint(input);

            var dns = endpoint as DnsEndPoint;
            Assert.IsNotNull(dns, $"Expected a DnsEndPoint for '{input}'");
            Assert.AreEqual(expectedHost, dns!.Host);
            Assert.AreEqual(expectedPort, dns.Port);
        }

        // Inputs whose token count is wrong throw the parser's own Exception (not a derived type),
        // so ThrowsException<Exception> is an exact match. (ParseForwardString is deliberately NOT
        // tested for invalid input: it calls Environment.Exit(1), which would kill the test host —
        // see the note in ParseTests.)
        [DataTestMethod]
        [DataRow("")]
        [DataRow("garbage")]
        [DataRow("a:b:c")]
        [DataRow("1.2.3.4:5:6:7")]
        public void ParseEndpoint_InvalidInput_Throws(string input)
        {
            try
            {
                NetworkUtilities.ParseEndpoint(input);
            }
            catch
            {
                return; // expected
            }

            Assert.Fail($"Expected ParseEndpoint('{input}') to throw, but it returned normally");
        }
    }
}
