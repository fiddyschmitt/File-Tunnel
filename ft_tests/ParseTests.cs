using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using ft_tests.Utilities;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;
using ft.Utilities;

namespace ft_tests
{
    [TestClass]
    [TestCategory("Unit")]
    public partial class ParseTests
    {
        // NOTE: only valid inputs are covered here. ParseForwardString cannot be negative-tested
        // because on malformed input it calls Environment.Exit(1) (NetworkUtilities.cs), which would
        // kill the test host. Invalid-input coverage therefore lives in CoreUnitTests against the
        // sibling ParseEndpoint, which throws instead. Making ParseForwardString throw (so its error
        // paths become testable) is a recommended follow-up ft change.
        [DataTestMethod]
        [DataRow("5000:192.168.0.20:3389",          "127.0.0.1:5000", "192.168.0.20:3389")]
        [DataRow("5000:server01:3389",              "127.0.0.1:5000", "server01:3389")]
        [DataRow("127.0.0.1:5000:192.168.0.20:3389","127.0.0.1:5000", "192.168.0.20:3389")]
        [DataRow("0.0.0.0:5000:192.168.0.20:3389",  "0.0.0.0:5000",  "192.168.0.20:3389")]
        [DataRow("*:5000:192.168.0.20:3389",        "*:5000",         "192.168.0.20:3389")]
        [DataRow("5000/192.168.0.20/3389",          "[::1]:5000",     "192.168.0.20:3389")]
        [DataRow("5000/server01/3389",              "[::1]:5000",     "server01:3389")]
        [DataRow("::1/5000/192.168.0.20/3389",      "[::1]:5000",     "192.168.0.20:3389")]
        [DataRow("::/5000/192.168.0.20/3389",       "[::]:5000",      "192.168.0.20:3389")]
        public void ParseForwardString(string input, string expectedListen, string expectedDest)
        {
            (var l, var d) = NetworkUtilities.ParseForwardString(input);
            Assert.AreEqual(expectedListen, l);
            Assert.AreEqual(expectedDest, d);
        }

        // Listen-only parsing for dynamic (SOCKS) forwards. Unlike ParseForwardString this throws on bad
        // input rather than Environment.Exit, so both the valid and invalid paths are testable here.
        [DataTestMethod]
        [DataRow("1080",           "127.0.0.1:1080")]
        [DataRow("0.0.0.0:1080",   "0.0.0.0:1080")]
        [DataRow("127.0.0.1:1080", "127.0.0.1:1080")]
        [DataRow("::1/1080",       "[::1]:1080")]
        public void ParseListenOnlyString(string input, string expected)
        {
            Assert.AreEqual(expected, NetworkUtilities.ParseListenOnlyString(input));
        }

        [DataTestMethod]
        [DataRow("1080:host:22")]
        [DataRow("0.0.0.0:1080:host:22")]
        public void ParseListenOnlyString_Malformed_Throws(string input)
        {
            Assert.ThrowsExactly<System.Exception>(() => NetworkUtilities.ParseListenOnlyString(input));
        }

        // A bare [bind:]port is a dynamic (SOCKS) forward; a full [bind:]port:host:hostport is not.
        [DataTestMethod]
        [DataRow("1080",                 true)]
        [DataRow("0.0.0.0:1080",         true)]
        [DataRow("::1/1080",             true)]
        [DataRow("1080:host:22",         false)]
        [DataRow("0.0.0.0:1080:host:22", false)]
        [DataRow("::1/1080/host/22",     false)]
        public void IsDynamicForwardSpec_Classifies(string spec, bool expected)
        {
            Assert.AreEqual(expected, NetworkUtilities.IsDynamicForwardSpec(spec));
        }
    }
}