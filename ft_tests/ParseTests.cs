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
        [TestMethod]
        public void ParseForwardString()
        {
            (var l, var d) = NetworkUtilities.ParseForwardString("5000:192.168.0.20:3389");
            Assert.AreEqual(l, "127.0.0.1:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");

            (l, d) = NetworkUtilities.ParseForwardString("5000:server01:3389");
            Assert.AreEqual(l, "127.0.0.1:5000");
            Assert.AreEqual(d, "server01:3389");

            (l, d) = NetworkUtilities.ParseForwardString("127.0.0.1:5000:192.168.0.20:3389");
            Assert.AreEqual(l, "127.0.0.1:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");

            (l, d) = NetworkUtilities.ParseForwardString("0.0.0.0:5000:192.168.0.20:3389");
            Assert.AreEqual(l, "0.0.0.0:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");

            (l, d) = NetworkUtilities.ParseForwardString("*:5000:192.168.0.20:3389");
            Assert.AreEqual(l, "*:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");



            (l, d) = NetworkUtilities.ParseForwardString("5000/192.168.0.20/3389");
            Assert.AreEqual(l, "[::1]:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");

            (l, d) = NetworkUtilities.ParseForwardString("5000/server01/3389");
            Assert.AreEqual(l, "[::1]:5000");
            Assert.AreEqual(d, "server01:3389");

            (l, d) = NetworkUtilities.ParseForwardString("::1/5000/192.168.0.20/3389");
            Assert.AreEqual(l, "[::1]:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");

            (l, d) = NetworkUtilities.ParseForwardString("::/5000/192.168.0.20/3389");
            Assert.AreEqual(l, "[::]:5000");
            Assert.AreEqual(d, "192.168.0.20:3389");
        }
    }
}