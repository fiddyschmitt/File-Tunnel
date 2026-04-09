using CsvHelper;
using CsvHelper.Configuration;
using ft;
using ft_tests.FileShares.Clients;
using ft_tests.FileShares.Servers;
using ft_tests.Runner;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ft_tests
{
    [TestClass]
    public class EndToEndTests
    {
        const string WIN_X64_EXE = @"R:\Temp\ft release\win-x64\ft.exe";
        const string LINUX_X64_EXE = @"R:\Temp\ft release\linux-x64\ft";
        static string localWindowsOutputFilename = "";

        static ProcessRunner win10_x64_1;
        static ProcessRunner win10_x64_2;
        static ProcessRunner win10_x64_3;


        static ProcessRunner linux_x64_1;
        static ProcessRunner linux_x64_2;
        static ProcessRunner linux_x64_3;

        static CsvWriter csvWriter;

        static int testNumber = 0;
        static readonly Stopwatch totalDuration = new();
        static double totalCpuUsageMs = 0;

        [ClassInitialize]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
        public static void ClassInit(TestContext context)
        {
            var config = new ConfigurationBuilder()
                                .AddUserSecrets<EndToEndTests>()
                                .Build();


            var testResultsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_results");
            Directory.CreateDirectory(testResultsFolder);

            var justDateFilename = $"{DateTime.Now:yyyy-MM-dd HHmm ss}";
            var testResultsFilename = Path.Combine(testResultsFolder, $"{justDateFilename}.csv");
            localWindowsOutputFilename = Path.ChangeExtension(testResultsFilename, ".log");
            var remoteLinuxOutputFilename = $"/media/smb/192.168.0.31/r/Temp/ft release/linux-x64/output/{justDateFilename}";



            win10_x64_1 = new LocalWindowsProcessRunner(WIN_X64_EXE, localWindowsOutputFilename);
            win10_x64_2 = new RemoteWindowsProcessRunner("192.168.0.32", config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE); //win10 VM
            win10_x64_3 = new RemoteWindowsProcessRunner("192.168.0.20", config["edm_username"], config["edm_password"], WIN_X64_EXE);          //elitedesk

            linux_x64_1 = new LinuxProcessRunner("192.168.0.80", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.80.log");
            linux_x64_2 = new LinuxProcessRunner("192.168.0.81", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.81.log");
            linux_x64_3 = new LinuxProcessRunner("192.168.0.82", "user", "live", LINUX_X64_EXE, remoteLinuxOutputFilename + " 192.168.0.82.log");


            var writer = new StreamWriter(testResultsFilename)
            {
                AutoFlush = true
            };
            csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
            });

            csvWriter.WriteField("test_num");

            csvWriter.WriteField("result");
            csvWriter.WriteField("duration");


            csvWriter.WriteField("file_share_type");
            csvWriter.WriteField("mode");
            csvWriter.WriteField("client_1");
            csvWriter.WriteField("server");
            csvWriter.WriteField("client_2");

            csvWriter.WriteField("total_processor_time_ms_1");
            csvWriter.WriteField("total_processor_time_ms_2");

            csvWriter.WriteField("command_1");
            csvWriter.WriteField("command_2");

            csvWriter.WriteField("error_message");

            csvWriter.Flush();

            totalDuration.Start();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            totalDuration.Stop();

            csvWriter.NextRecord();

            csvWriter.WriteField("");
            csvWriter.WriteField("");

            csvWriter.WriteField($"{totalDuration.Elapsed.TotalSeconds:0.000}");

            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");
            csvWriter.WriteField("");

            csvWriter.WriteField($"{totalCpuUsageMs.ToString("0", CultureInfo.InvariantCulture)}");

            csvWriter.Flush();
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void Smb(OS client1OS, OS serverOS, OS client2OS, Mode mode)
        {
            SmbServer smbServer = serverOS == OS.Linux
                ? new SmbServer(OS.Linux, linux_x64_2)
                : new SmbServer(OS.Windows, win10_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = SmbPathLookup(client1OS, serverOS, filename1);
            var readPath1 = SmbPathLookup(client1OS, serverOS, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1}");

            var readPath2 = SmbPathLookup(client2OS, serverOS, filename1);
            var writePath2 = SmbPathLookup(client2OS, serverOS, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, smbServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string SmbPathLookup(OS client, OS server, string fileName)
        {
            var clientSep = client == OS.Windows ? '\\' : '/';
            var otherSep = client == OS.Windows ? '/' : '\\';
            fileName = fileName.Replace(otherSep, clientSep).TrimStart('\\', '/');

            string basePath = (client, server) switch
            {
                (OS.Windows, OS.Windows) => @$"\\192.168.0.32\shared\",
                (OS.Windows, OS.Linux) => @$"\\192.168.0.81\data\",
                (OS.Linux, OS.Windows) => @$"/media/smb/192.168.0.32/shared/",
                (OS.Linux, OS.Linux) => @$"/media/smb/192.168.0.81/data/",
                _ => throw new InvalidOperationException("Unsupported client/server OS combo")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Windows, Mode.Normal)]
        [DataRow(OS.Linux, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void Nfs(OS client1OS, OS client2OS, Mode mode)
        {
            var nfsServer = new NfsServer(linux_x64_2);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = NfsPathLookup(client1OS, filename1);
            var readPath1 = NfsPathLookup(client1OS, filename2);
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new NfsClient(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = NfsPathLookup(client2OS, filename1);
            var writePath2 = NfsPathLookup(client2OS, filename2);
            var client2Runner = client2OS == OS.Windows ? win10_x64_3 : linux_x64_3;
            var side2 = new NfsClient(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, nfsServer, side2, readPath1, writePath1, readPath2, writePath2);
        }

        private static string NfsPathLookup(OS client, string fileName)
        {
            var clientSep = client == OS.Windows ? '\\' : '/';
            var otherSep = client == OS.Windows ? '/' : '\\';
            fileName = fileName.Replace(otherSep, clientSep).TrimStart('\\', '/');

            string basePath = client switch
            {
                OS.Windows => @"X:\",
                OS.Linux => "/media/nfs/192.168.0.81/tmpfs/",
                _ => throw new InvalidOperationException("Unsupported client OS")
            };

            if (!basePath.EndsWith(clientSep)) basePath += clientSep;
            return basePath + fileName;
        }

        [DataTestMethod]
        [DataRow(Mode.Normal)]
        [DataRow(Mode.IsolatedReads)]
        public void Rdp(Mode mode)
        {
            var server = new Server(OS.Windows, FileShareType.RDP);

            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = $@"C:\Temp\{filename1}";
            var readPath1 = $@"C:\Temp\{filename2}";
            var side1 = new Client(OS.Windows, win10_x64_1, $"-w {writePath1} -r {readPath1}");

            var readPath2 = $@"\\tsclient\c\Temp\{filename1}";
            var writePath2 = $@"\\tsclient\c\Temp\{filename2}";
            var side2 = new Client(OS.Windows, win10_x64_3, $"-r {readPath2} -w {writePath2}");

            ConductTunnelTests(mode, side1, server, side2, readPath1, writePath1, readPath2, writePath2);
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows, Mode.Normal)]
        [DataRow(OS.Windows, OS.Windows, Mode.IsolatedReads)]
        [DataRow(OS.Windows, OS.Linux, Mode.Normal)]
        [DataRow(OS.Windows, OS.Linux, Mode.IsolatedReads)]
        [DataRow(OS.Linux, OS.Linux, Mode.Normal)]
        [DataRow(OS.Linux, OS.Linux, Mode.IsolatedReads)]
        public void VirtualBoxSharedFolder(OS client1OS, OS client2OS, Mode mode)
        {
            var filename1 = $"{Random.Shared.Next(int.MaxValue)}.dat";
            var filename2 = $"{Random.Shared.Next(int.MaxValue)}.dat";

            var writePath1 = client1OS switch
            {
                OS.Windows => $@"C:\Temp\{filename1}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename1}"
            };
            var readPath1 = client1OS switch
            {
                OS.Windows => $@"C:\Temp\{filename2}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename2}"
            };
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"-w {writePath1} -r {readPath1} --verbose");

            var readPath2 = client2OS switch
            {
                OS.Windows => $@"\\vboxsvr\c_drive\Temp\{filename1}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename1}"
            };
            var writePath2 = client2OS switch
            {
                OS.Windows => $@"\\vboxsvr\c_drive\Temp\{filename2}",
                OS.Linux => $@"/media/vboxsf/192.168.0.31/c_drive/Temp/{filename2}"
            };
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"-r {readPath2} -w {writePath2} --verbose");

            ConductTunnelTests(mode, side1, new Server(OS.Windows, FileShareType.VirtualBoxSharedFolder), side2, readPath1, writePath1, readPath2, writePath2);
        }

        [DataTestMethod]
        [DataRow(OS.Windows, OS.Windows)]
        [DataRow(OS.Windows, OS.Linux)]
        [DataRow(OS.Linux, OS.Linux)]
        public void FTP(OS client1OS, OS client2OS)
        {
            var writePath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var readPath1 = $"uploads/{Random.Shared.Next(int.MaxValue)}.dat";
            var client1Runner = client1OS == OS.Windows ? win10_x64_1 : linux_x64_1;
            var side1 = new Client(client1OS, client1Runner, $"--ftp -u anonymous -h 192.168.0.81 -w \"{writePath1}\" -r \"{readPath1}\" --verbose");

            var readPath2 = writePath1;
            var writePath2 = readPath1;
            var client2Runner = client2OS == OS.Windows ? win10_x64_2 : linux_x64_3;
            var side2 = new Client(client2OS, client2Runner, $"--ftp -u anonymous -h 192.168.0.81 -r \"{readPath2}\" -w \"{writePath2}\" --verbose");

            ConductTunnelTests(Mode.FTP, side1, new Server(OS.Linux, FileShareType.FTP), side2, readPath1, writePath1, readPath2, writePath2);
        }


        public static void ConductTunnelTests(Mode mode, Client side1, Server server, Client side2, string readPath1, string writePath1, string readPath2, string writePath2)
        {
            //if ((server.FileShareType == FileShareType.NFS && (side1.OS == OS.Windows && server.OS == OS.Linux && side2.OS == OS.Windows)) == false) return;

            var cleanupFiles = new Action(() =>
            {
                Task[] deleteTasks = [
                    Task.Factory.StartNew(() => side1.Runner.DeleteFile(readPath1)),
                    Task.Factory.StartNew(() => side1.Runner.DeleteFile(writePath1)),
                    Task.Factory.StartNew(() => side2.Runner.DeleteFile(readPath2)),
                    Task.Factory.StartNew(() => side2.Runner.DeleteFile(writePath2))];

                try
                {
                    Task.WaitAll(deleteTasks, 10000);
                }
                catch { }
            });

            var name = $"{server.FileShareType} {side1.OS}-{server.OS}-{side2.OS}";


            if (mode == Mode.Normal)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (Normal mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "Normal");
            }



            if (mode == Mode.IsolatedReads)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (Isolated Reads mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004 --isolated-reads"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args} --isolated-reads"),
                        "Isolated Reads");
            }



            if (mode == Mode.UploadDownload)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (Upload-Download mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004 --upload-download --pace 100"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args} --upload-download --pace 100"),
                        "Upload-Download");
            }

            if (mode == Mode.FTP)
            {
                server.Restart();
                side1.Restart();
                side2.Restart();
                cleanupFiles();

                ConductTest(
                        $"{name} (FTP mode)",
                        new Client(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.0.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.0.31:5004"),
                        server,
                        new Client(side2.OS, side2.Runner, $"{side2.Args}"),
                        "FTP");
            }
        }

        public static void ConductTest(string name, Client side1, Server server, Client side2, string mode)
        {
            //if (!(side1.OS == OS.Windows && server.OS == OS.Linux && side2.OS == OS.Linux && mode == "Isolated Reads" && server.FileShareType == FileShareType.SMB)) return;

            var testNumberStr = $"Test {testNumber++}";
            File.AppendAllLines(localWindowsOutputFilename, [testNumberStr]);

            csvWriter.NextRecord();

            var sw = Stopwatch.StartNew();

            side1.Runner.Run(side1.Args);
            side2.Runner.Run(side2.Args);

            var results = new BlockingCollection<(bool Success, string Errror)>();

            var stop = new CancellationTokenSource();

            var transfersTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    TestTransfer(5 * 1024 * 1024, true, 2, side1.Runner.RunOnIP, stop.Token);
                    results.Add((true, ""));
                }
                catch (Exception ex)
                {
                    results.Add((false, ex.Message));
                }
            }, TaskCreationOptions.LongRunning);


            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));

            (bool Success, string Errror) result;
            try
            {
                result = results.Take(timeout.Token);
            }
            catch
            {
                result = (false, "Did not finish");
            }

            stop.Cancel();
            transfersTask.Wait();

            sw.Stop();

            csvWriter.WriteField(testNumberStr);

            if (result.Success)
            {
                Debug.WriteLine($@"""{name}"",""Pass"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"pass");
            }
            else
            {
                Debug.WriteLine($@"""{name}"",""Fail"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"fail");
            }

            csvWriter.WriteField($"{sw.Elapsed.TotalSeconds:N3}");

            csvWriter.WriteField($"{server.FileShareType}");
            csvWriter.WriteField($"{mode}");
            csvWriter.WriteField($"{side1.OS}");
            csvWriter.WriteField($"{server.OS}");
            csvWriter.WriteField($"{side2.OS}");



            var side1Duration = side1.Runner.Stop();
            var side2Duration = side2.Runner.Stop();

            csvWriter.WriteField(side1Duration?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "");
            csvWriter.WriteField(side2Duration?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "");

            totalCpuUsageMs += side1Duration?.TotalMilliseconds ?? 0;
            totalCpuUsageMs += side2Duration?.TotalMilliseconds ?? 0;


            var command1 = side1.Runner.GetFullCommand(side1.Args);
            csvWriter.WriteField(command1);

            var command2 = side2.Runner.GetFullCommand(side2.Args);
            csvWriter.WriteField(command2);


            if (result.Success)
            {
                csvWriter.WriteField($"");
            }
            else
            {
                csvWriter.WriteField(result.Errror);
            }

            csvWriter.Flush();



            File.AppendAllLines(localWindowsOutputFilename, ["--------------------------------------------------------------------------------"]);

            Assert.IsTrue(result.Success);
        }

        public static (TcpClient connected, TcpClient accepted) EstablishConnection(TcpListener listener, IPEndPoint connectTo, CancellationToken cancelationToken)
        {
            var acceptConnectionTask = listener.AcceptTcpClientAsync(cancelationToken);

            var originClient = new TcpClient();

            var startTime = DateTime.Now;
            while (!cancelationToken.IsCancellationRequested)
            {
                var duration = DateTime.Now - startTime;
                if (duration.TotalSeconds > 150)
                {
                    throw new Exception("Could not connect");
                }

                try
                {
                    originClient.Connect(connectTo);
                }
                catch
                {
                    Thread.Sleep(200);
                    continue;
                }

                break;
            }


            while (!acceptConnectionTask.IsCompleted && acceptConnectionTask.IsCompletedSuccessfully && !cancelationToken.IsCancellationRequested)
            {
                Thread.Sleep(200);
            }
            var acceptedConnection = acceptConnectionTask.Result;

            return (originClient, acceptedConnection);
        }


        public static void TestTransfer(int bytesToSend, bool fullDuplex, int connections, string connectToIP, CancellationToken cancelationToken)
        {
            var ultimateDestination = new TcpListener($"0.0.0.0:5004".AsEndpoint());
            ultimateDestination.Start();

            try
            {
                var establishedConnections = Enumerable
                                                .Range(0, connections)
                                                .Select(connection =>
                                                {
                                                    var connectTo = $"{connectToIP}:5002".AsEndpoint();
                                                    (var originClient, var ultimateDestinationClient) = EstablishConnection(ultimateDestination, connectTo, cancelationToken);

                                                    Debug.WriteLine($"Accepted connection from: {ultimateDestinationClient.Client.RemoteEndPoint}");

                                                    return new
                                                    {
                                                        OriginClient = originClient,
                                                        UltimateDestinationClient = ultimateDestinationClient
                                                    };
                                                })
                                                .ToList();

                if (cancelationToken.IsCancellationRequested)
                {
                    throw new Exception($"Connections were not established within the timeout window");
                }

                establishedConnections
                    .AsParallel()
                    .WithDegreeOfParallelism(connections)
                    .ForAll(pair =>
                    {
                        var toSend = new byte[bytesToSend];
                        Random.Shared.NextBytes(toSend);

                        var tests = new[]
                        {
                            new Action(() => TestDirection("Forward", pair.OriginClient, pair.UltimateDestinationClient, toSend)),
                            new Action(() => TestDirection("Reverse", pair.UltimateDestinationClient, pair.OriginClient, toSend)),
                        };

                        if (fullDuplex)
                        {
                            var testTasks = tests
                                                .ToList()
                                                .Select(test => Task.Factory.StartNew(test, TaskCreationOptions.LongRunning))
                                                .ToArray();

                            Task.WaitAll(testTasks, cancelationToken);
                        }
                        else
                        {
                            foreach (var test in tests)
                            {
                                test();
                            }
                        }
                    });

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                throw;
            }
            finally
            {
                ultimateDestination.Stop();
            }
        }

        static (bool Success, string Error) TestDirection(string direction, TcpClient sender, TcpClient receiver, byte[] toSend)
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
                    break;
                }

                totalRead += read;
            }

            var receivedSuccessfully = received.SequenceEqual(toSend);
            //Assert.IsTrue(receivedSuccessfully, $"[{direction}] Received buffer does not match sent buffer");

            if (receivedSuccessfully)
            {
                return (receivedSuccessfully, "");
            }
            else
            {
                return (receivedSuccessfully, $"[{direction}] Received buffer does not match sent buffer");
            }
        }
    }

    public enum OS
    {
        Windows,
        Linux,
        Mac
    }

    public enum FileShareType
    {
        SMB,
        NFS,
        FTP,

        RDP,

        VirtualBoxSharedFolder
    }

    public enum Mode
    {
        Normal,
        IsolatedReads,
        UploadDownload,
        FTP
    }
}
