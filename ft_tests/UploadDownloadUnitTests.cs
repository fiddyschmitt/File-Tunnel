using ft;
using ft.CLI;
using ft.IO.Files;
using ft.Listeners;
using ft.Utilities;
using ft_tests.Utilities;
using System.Diagnostics;
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
    /// command payload and the saving is largest, is otherwise only reachable through the lab rows;
    /// <see cref="UploadDownload_SingleSlot_EndsStartedTogether_DontStall"/> covers its slot handoff.
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

        // The single-slot handoff used by FTP, WebDAV, S3 and Dropbox: an end may write its next file only
        // once the counterpart has read and deleted its last one. Two ends started together, over a
        // server whose deletes take a while, used to wait on each other until the writer's fallback check
        // at half the tunnel timeout, because each checked for its own file before deleting the one it
        // had read. They must come online well before that fallback could fire.
        [TestMethod]
        [Timeout(60000)]
        public void UploadDownload_SingleSlot_EndsStartedTogether_DontStall()
        {
            const int tunnelTimeoutMs = 10000;   // the fallback check would fire after 5 s
            var (pace, writeInterval, readInterval) = (Options.PaceMilliseconds, Options.WriteIntervalMilliseconds, Options.ReadIntervalMilliseconds);
            Options.PaceMilliseconds = 10;
            Options.WriteIntervalMilliseconds = 0;
            Options.ReadIntervalMilliseconds = 0;

            var server = new SlowDeleteStore(TimeSpan.FromMilliseconds(300));
            try
            {
                var a = new UploadDownload(server, "b.dat", "a.dat", tunnelTimeoutMs, 1, 0, true, false);
                var b = new UploadDownload(server, "a.dat", "b.dat", tunnelTimeoutMs, 1, 0, true, false);
                var started = Stopwatch.StartNew();
                a.Start();
                b.Start();

                while (!(a.IsOnline && b.IsOnline) && started.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Thread.Sleep(50);
                }

                Assert.IsTrue(a.IsOnline && b.IsOnline, "The tunnel never came online");
                Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(4),
                    $"Both ends took {started.Elapsed.TotalSeconds:N1} s to come online: they waited on each other until the fallback check");
            }
            finally
            {
                server.Close();   // parks the ends' pump threads, which never exit on their own
                (Options.PaceMilliseconds, Options.WriteIntervalMilliseconds, Options.ReadIntervalMilliseconds) = (pace, writeInterval, readInterval);
            }
        }

        // An in-memory server whose deletes take effect only after a delay, like a remote one's. Once
        // closed, every call blocks for good.
        private sealed class SlowDeleteStore(TimeSpan deleteDelay) : IFileAccess
        {
            private readonly Dictionary<string, byte[]> files = [];
            private readonly ManualResetEventSlim closed = new(false);

            public void Close() => closed.Set();

            private void ParkIfClosed()
            {
                if (closed.IsSet) Thread.Sleep(Timeout.Infinite);
            }

            public bool Exists(string path)
            {
                ParkIfClosed();
                lock (files) return files.ContainsKey(path);
            }

            public void Delete(string path)
            {
                ParkIfClosed();
                Thread.Sleep(deleteDelay);
                lock (files) files.Remove(path);
            }

            public void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes, bool overwrite = true)
            {
                ParkIfClosed();
                lock (files) files[path] = bytes.ToArray();
            }

            public void Move(string sourceFileName, string destFileName, bool overwrite)
            {
                ParkIfClosed();
                lock (files)
                {
                    if (!files.Remove(sourceFileName, out var content)) throw new FileNotFoundException(sourceFileName);
                    files[destFileName] = content;
                }
            }

            public byte[] ReadAllBytes(string path)
            {
                ParkIfClosed();
                lock (files) return files.TryGetValue(path, out var content) ? content.ToArray() : throw new FileNotFoundException(path);
            }

            public long GetFileSize(string path)
            {
                ParkIfClosed();
                lock (files) return files.TryGetValue(path, out var content) ? content.Length : throw new FileNotFoundException(path);
            }
        }
    }
}
