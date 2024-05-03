using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using ft_tests.Utilities;

namespace ft_tests
{
    [DoNotParallelize]
    [TestClass]
    public partial class TcpUnitTests
    {
        [TestMethod]
        public void SmallTransfer()
        {
            var data = new byte[500];

            var random = new Random();
            random.NextBytes(data);

            TestTransfer(data, "127.0.0.1:5000", "127.0.0.1:8000", Path.GetTempFileName(), Path.GetTempFileName());
        }

        [TestMethod]
        public void MediumTransfer()
        {
            var data = new byte[50 * 1024 * 1024];

            var random = new Random();
            random.NextBytes(data);

            TestTransfer(data, "127.0.0.1:5000", "127.0.0.1:8000", Path.GetTempFileName(), Path.GetTempFileName());
        }

        [TestMethod]
        public void LargeTransfer()
        {
            var data = new byte[500 * 1024 * 1024];

            var random = new Random();
            random.NextBytes(data);

            TestTransfer(data, "127.0.0.1:5000", "127.0.0.1:8000", Path.GetTempFileName(), Path.GetTempFileName());
        }

        public static void TestTransfer(byte[] toSend, string listenPoint, string connectPoint, string writeFilename, string readFilename)
        {
            var listenThread = new Thread(() =>
            {
                var listenArgsString = $@"--tcp-listen {listenPoint} --write ""{writeFilename}"" --read ""{readFilename}""";
                var listenArgs = StringUtility.CommandLineToArgs(listenArgsString);
                ft.Program.Main(listenArgs);
            });
            listenThread.Start();

            var forwardThread = new Thread(() =>
            {
                var forwardArgsString = $@"--read {writeFilename} --tcp-connect {connectPoint} --write ""{readFilename}""";
                var forwardArgs = StringUtility.CommandLineToArgs(forwardArgsString);
                ft.Program.Main(forwardArgs);

            });
            forwardThread.Start();

            var ultimateDestination = new TcpListener(IPEndPoint.Parse(connectPoint));
            ultimateDestination.Start();
            var ultimateDestinationClientTask = ultimateDestination.AcceptTcpClientAsync();

            var originClient = new TcpClient();
            originClient.Connect(IPEndPoint.Parse(listenPoint));

            var ultimateDestinationClient = ultimateDestinationClientTask.Result;

            TestDirection("Forward", originClient, ultimateDestinationClient, toSend);
            TestDirection("Reverse", ultimateDestinationClient, originClient, toSend);

            originClient.Close();
            ultimateDestination.Stop();

            listenThread.Interrupt();
            listenThread.Join();

            forwardThread.Interrupt();
            forwardThread.Join();

            File.Delete(readFilename);
            File.Delete(writeFilename);
        }

        static void TestDirection(string direction, TcpClient sender, TcpClient receiver, byte[] toSend)
        {
            sender.GetStream().Write(toSend, 0, toSend.Length);

            var received = new byte[toSend.Length];
            receiver.GetStream().ReadAtLeast(received, received.Length, false);

            var receivedSuccessfully = received.SequenceEqual(toSend);
            Assert.IsTrue(receivedSuccessfully, $"[{direction}] Received buffer does not match sent buffer");
        }
    }
}