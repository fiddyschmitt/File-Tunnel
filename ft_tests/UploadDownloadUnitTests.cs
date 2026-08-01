using ft;
using ft.Utilities;
using ft_tests.Utilities;
using System.Net.Sockets;

namespace ft_tests
{
    /// <summary>
    /// In-process end-to-end test of the <b>UploadDownload</b> transport (ft's --upload-download mode),
    /// run over local temp files so no lab is needed. Until now every UploadDownload test lived in
    /// EndToEndTests and needed real VMs, an FTP/WebDAV/S3 server or Dropbox credentials - so the mode had
    /// no hermetic coverage at all.
    ///
    /// This drives the ranged-read path added alongside it: establishing a session runs the subfile
    /// candidate scan, which reads each subfile's 16-byte header via <see cref="ft.IO.Files.IFileAccess.ReadBytes"/>,
    /// and the reader re-reads the session id periodically thereafter. maxSubfiles is 5 on this path, so
    /// it also covers the subfile rotation and the reorder buffer.
    ///
    /// The single-subfile case (FTP/WebDAV/S3/Dropbox), where that header shares a file with the whole
    /// command payload and the saving is largest, is still only reachable through the lab rows.
    /// </summary>
    [DoNotParallelize]
    [TestClass]
    [TestCategory("Unit")]
    public class UploadDownloadUnitTests
    {
        [TestMethod]
        [Timeout(180000)]
        public void UploadDownload_Transfers()
        {
            const string forwardStr = "5501:127.0.0.1:8501";
            const int payloadBytes = 1024 * 1024;

            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();

            var listenThread = new Thread(() => ft.Program.Main(StringUtility.CommandLineToArgs(
                $@"--upload-download -L {forwardStr} --write ""{writeFilename}"" --read ""{readFilename}""")));
            listenThread.Start();

            var forwardThread = new Thread(() => ft.Program.Main(StringUtility.CommandLineToArgs(
                $@"--upload-download --read ""{writeFilename}"" --write ""{readFilename}""")));
            forwardThread.Start();

            var (listenEndpoint, destinationEndpoint) = NetworkUtilities.ParseForwardString(forwardStr);

            var ultimateDestination = new TcpListener(destinationEndpoint.AsEndpoint());
            ultimateDestination.Start();

            TcpClient? originClient = null;
            TcpClient? destinationClient = null;

            try
            {
                var acceptTask = Task.Factory.StartNew(ultimateDestination.AcceptTcpClient, TaskCreationOptions.LongRunning);

                originClient = new TcpClient();
                var startTime = DateTime.Now;
                while (true)
                {
                    if ((DateTime.Now - startTime).TotalSeconds > 60)
                    {
                        throw new Exception($"Could not connect to {listenEndpoint} - the tunnel never came online");
                    }

                    try { originClient.Connect(listenEndpoint.AsEndpoint()); break; }
                    catch { Thread.Sleep(200); }
                }

                Assert.IsTrue(acceptTask.Wait(TimeSpan.FromSeconds(60)), "The exit side never dialed the destination");
                destinationClient = acceptTask.Result;

                var payload = new byte[payloadBytes];
                Random.Shared.NextBytes(payload);

                TransferVerification.TestDirection("Forward", originClient, destinationClient, payload);
                TransferVerification.TestDirection("Reverse", destinationClient, originClient, payload);
            }
            finally
            {
                try { originClient?.Close(); } catch { }
                try { destinationClient?.Close(); } catch { }
                try { ultimateDestination.Stop(); } catch { }

                listenThread.Interrupt();
                listenThread.Join();
                forwardThread.Interrupt();
                forwardThread.Join();

                // --upload-download fans out into <name>.ft<n>.<ext> subfiles beside the two named files.
                foreach (var filename in new[] { readFilename, writeFilename })
                {
                    try { File.Delete(filename); } catch { }

                    var directory = Path.GetDirectoryName(filename);
                    if (directory == null) continue;

                    foreach (var subfile in Directory.GetFiles(directory, $"{Path.GetFileNameWithoutExtension(filename)}.ft*"))
                    {
                        try { File.Delete(subfile); } catch { }
                    }
                }
            }
        }
    }
}
