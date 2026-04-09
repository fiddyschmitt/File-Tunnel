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
    public partial class ParseTests
    {
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
    }
}