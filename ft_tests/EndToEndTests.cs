using CsvHelper;
using CsvHelper.Configuration;
using ft;
using ft_tests.Runner;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;

namespace ft_tests
{
    [TestClass]
    public class EndToEndTests
    {
        const string WIN_X64_EXE = @"R:\Temp\ft release\win-x64\ft.exe";
        const string LINUX_X64_EXE = @"R:\Temp\ft release\linux-x64\ft";

        static ProcessRunner win10_x64_1;
        //static ProcessRunner win10_x64_2;
        static ProcessRunner win10_x64_3;


        static ProcessRunner linux_x64_1;
        //static ProcessRunner linux_x64_2;
        static ProcessRunner linux_x64_3;

        public CsvWriter csvWriter;

        public EndToEndTests()
        {
            var config = new ConfigurationBuilder()
                                .AddUserSecrets<EndToEndTests>()
                                .Build();

            win10_x64_1 = new LocalWindowsProcessRunner(WIN_X64_EXE);
            //win10_x64_2 = new WindowsProcessRunner("192.168.1.5", config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE);                 //win10 VM
            win10_x64_3 = new RemoteWindowsProcessRunner("192.168.1.20", config["edm_username"], config["edm_password"], WIN_X64_EXE);        //elitedesk

            linux_x64_1 = new LinuxProcessRunner("192.168.1.80", "user", "live", LINUX_X64_EXE, "/user/home/");
            //linux_x64_2 = new LinuxProcessRunner("192.168.1.81", "user", "live", LINUX_X64_EXE, "/user/home/");
            linux_x64_3 = new LinuxProcessRunner("192.168.1.82", "user", "live", LINUX_X64_EXE, "/user/home/");

            var testResultsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_results");
            Directory.CreateDirectory(testResultsFolder);
            var testResultsFilename = Path.Combine(testResultsFolder, $"{DateTime.Now:yyyy-MM-dd HHmm ss}.csv");

            var writer = new StreamWriter(testResultsFilename)
            {
                AutoFlush = true
            };
            csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
            });

            csvWriter.WriteField("result");
            csvWriter.WriteField("error_message");
            csvWriter.WriteField("duration");

            csvWriter.WriteField("file_share_type");
            csvWriter.WriteField("mode");
            csvWriter.WriteField("client_1");
            csvWriter.WriteField("server");
            csvWriter.WriteField("client_2");
            csvWriter.WriteField("command_1");
            csvWriter.WriteField("command_2");

            csvWriter.Flush();
        }

        [TestMethod]
        public void Smb()
        {
            OS[] client1 = [OS.Windows, OS.Linux];
            OS[] servers = [OS.Windows, OS.Linux];
            OS[] client2 = [OS.Windows, OS.Linux];

            var combinations = Utilities.Extensions
                                .CartesianProduct([client1, servers, client2])
                                .Select(combo =>
                                {
                                    var lst = combo.ToList();
                                    return new
                                    {
                                        Client1 = lst[0],
                                        Server = new Server(lst[1], FileShareType.SMB),
                                        Client2 = lst[2]
                                    };
                                })
                                .ToList();

            var pathLookup = (OS client, OS server, string fileName) =>
            {
                var result = "";

                if (client == OS.Windows && server == OS.Windows) result = @$"\\192.168.1.5\shared\{fileName}";
                if (client == OS.Windows && server == OS.Linux) result = @$"\\192.168.1.81\data\{fileName}";
                if (client == OS.Linux && server == OS.Windows) result = @$"/media/smb/192.168.1.5/shared/{fileName}";
                if (client == OS.Linux && server == OS.Linux) result = @$"/media/smb/192.168.1.81/data/{fileName}";

                return result;
            };

            combinations
                .ForEach(combo =>
                {
                    var client1_process_runner = combo.Client1 switch
                    {
                        OS.Windows => win10_x64_1,
                        OS.Linux => linux_x64_1,
                        _ => throw new NotImplementedException()
                    };

                    var writePath1 = pathLookup(combo.Client1, combo.Server.OS, "1.dat");
                    var readPath1 = pathLookup(combo.Client1, combo.Server.OS, "2.dat");

                    var side1 = new Side(combo.Client1, client1_process_runner, $"-w {writePath1} -r {readPath1}");



                    var client2_process_runner = combo.Client2 switch
                    {
                        OS.Windows => win10_x64_3,
                        OS.Linux => linux_x64_3,
                        _ => throw new NotImplementedException()
                    };

                    var readPath2 = pathLookup(combo.Client2, combo.Server.OS, "1.dat");
                    var writePath2 = pathLookup(combo.Client2, combo.Server.OS, "2.dat");

                    var side2 = new Side(combo.Client2, client2_process_runner, $"-r {readPath2} -w {writePath2}");

                    ConductTunnelTests(side1, combo.Server, side2);
                });
        }

        Random random = new();

        [TestMethod]
        public void Nfs()
        {
            OS[] client1 = [OS.Windows, OS.Linux];
            OS[] servers = [OS.Linux];
            OS[] client2 = [OS.Windows, OS.Linux];

            var combinations = Utilities.Extensions
                                .CartesianProduct([client1, servers, client2])
                                .Select(combo =>
                                {
                                    var lst = combo.ToList();
                                    return new
                                    {
                                        Client1 = lst[0],
                                        Server = new Server(lst[1], FileShareType.NFS),
                                        Client2 = lst[2]
                                    };
                                })
                                .ToList();

            var pathLookup = (OS client, OS server, string fileName) =>
            {
                var result = "";

                if (client == OS.Windows && server == OS.Linux) result = @$"X:\{fileName}";     //Using X:\ works, but the alternative doesn't: \\192.168.1.81\mnt\tmpfs
                if (client == OS.Linux && server == OS.Linux) result = @$"/media/nfs/192.168.1.81/tmpfs/{fileName}";

                return result;
            };

            combinations
                .ForEach(combo =>
                {
                    var client1_process_runner = combo.Client1 switch
                    {
                        OS.Windows => win10_x64_1,
                        OS.Linux => linux_x64_1,
                        _ => throw new NotImplementedException()
                    };

                    var filename1 = $"{random.Next(int.MaxValue)}.dat";
                    var filename2 = $"{random.Next(int.MaxValue)}.dat";


                    var writePath1 = pathLookup(combo.Client1, combo.Server.OS, filename1);
                    var readPath1 = pathLookup(combo.Client1, combo.Server.OS, filename2);

                    var side1 = new Side(combo.Client1, client1_process_runner, $"-w {writePath1} -r {readPath1}");



                    var client2_process_runner = combo.Client2 switch
                    {
                        OS.Windows => win10_x64_3,
                        OS.Linux => linux_x64_3,
                        _ => throw new NotImplementedException()
                    };

                    var readPath2 = pathLookup(combo.Client2, combo.Server.OS, filename1);
                    var writePath2 = pathLookup(combo.Client2, combo.Server.OS, filename2);

                    var side2 = new Side(combo.Client2, client2_process_runner, $"-r {readPath2} -w {writePath2}");

                    ConductTunnelTests(side1, combo.Server, side2);
                });
        }

        public void ConductTunnelTests(Side side1, Server server, Side side2)
        {
            var name = $"{server.FileShareType} {side1.OS}-{server.OS}-{side2.OS}";

            ConductTest(
                    $"{name} (Normal mode)",
                    new Side(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.1.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.1.31:5004"),
                    server,
                    new Side(side2.OS, side2.Runner, $"{side2.Args}"),
                    "Normal");

            Thread.Sleep(5000);





            ConductTest(
                    $"{name} (Isolated Reads mode)",
                    new Side(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.1.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.1.31:5004 --isolated-reads"),
                    server,
                    new Side(side2.OS, side2.Runner, $"{side2.Args} --isolated-reads"),
                    "Isolated Reads");

            Thread.Sleep(5000);





            ConductTest(
                    $"{name} (Upload-Download mode)",
                    new Side(side1.OS, side1.Runner, $"{side1.Args} -L 0.0.0.0:5001:192.168.1.20:6000 -L 0.0.0.0:5002:127.0.0.1:5003 -R 5003:192.168.1.31:5004 --upload-download"),
                    server,
                    new Side(side2.OS, side2.Runner, $"{side2.Args} --upload-download"),
                    "Upload-Download");

            Thread.Sleep(5000);
        }

        public void ConductTest(string name, Side side1, Server server, Side side2, string mode)
        {
            //if (!(side1.OS == OS.Windows && server.OS == OS.Linux && side2.OS == OS.Linux && mode == "Isolated Reads" && server.FileShareType == FileShareType.SMB)) return;

            csvWriter.NextRecord();

            var sw = Stopwatch.StartNew();

            side1.Runner.Run(side1.Args);
            side2.Runner.Run(side2.Args);

            var results = new BlockingCollection<(bool Success, string Errror)>();

            var stop = new CancellationTokenSource();

            Task.Factory.StartNew(() =>
            {
                try
                {
                    TestTransfer(5 * 1024 * 1024, true, 10, side1.Runner.RunOnIP, stop);
                    results.Add((true, ""));
                }
                catch (Exception ex)
                {
                    results.Add((false, ex.Message));
                }
            });


            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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
            sw.Stop();

            if (result.Success)
            {
                Debug.WriteLine($@"""{name}"",""Pass"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"pass");
                csvWriter.WriteField($"{sw.Elapsed.TotalSeconds:N3}");
                csvWriter.WriteField($"");
            }
            else
            {
                Debug.WriteLine($@"""{name}"",""Fail"",""{sw.Elapsed.TotalSeconds:N3}""");

                csvWriter.WriteField($"fail");
                csvWriter.WriteField($"{sw.Elapsed.TotalSeconds:N3}");
                csvWriter.WriteField(result.Errror);
            }

            csvWriter.WriteField($"{server.FileShareType}");
            csvWriter.WriteField($"{mode}");
            csvWriter.WriteField($"{side1.OS}");
            csvWriter.WriteField($"{server.OS}");
            csvWriter.WriteField($"{side2.OS}");


            var command1 = side1.Runner.GetFullCommand(side1.Args);
            csvWriter.WriteField(command1);

            var command2 = side2.Runner.GetFullCommand(side2.Args);
            csvWriter.WriteField(command2);
            csvWriter.Flush();

            side1.Runner.Stop();
            side2.Runner.Stop();
        }

        public static void TestTransfer(int bytesToSend, bool fullDuplex, int connections, string connectToIP, CancellationTokenSource cancelationToken)
        {
            var ultimateDestination = new TcpListener($"0.0.0.0:5004".AsEndpoint());

            try
            {
                ultimateDestination.Start();                
                var ultimateDestinationClients = new BlockingCollection<TcpClient>();

                Task.Factory.StartNew(() =>
                {
                    while (!cancelationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var client = ultimateDestination.AcceptTcpClientAsync(cancelationToken.Token).Result;
                            ultimateDestinationClients.Add(client);
                        }
                        catch
                        {
                            ultimateDestination.Stop();
                            break;
                        }
                    }
                }, TaskCreationOptions.LongRunning);

                Enumerable
                    .Range(0, connections)
                    .Select(connection =>
                    {
                        var originClient = new TcpClient();

                        var startTime = DateTime.Now;

                        while (!cancelationToken.IsCancellationRequested)
                        {
                            var duration = DateTime.Now - startTime;
                            if (duration.TotalSeconds > 22)
                            {
                                throw new Exception("Could not connect");
                            }

                            try
                            {
                                originClient.Connect($"{connectToIP}:5002".AsEndpoint());
                            }
                            catch
                            {
                                Thread.Sleep(200);
                                continue;
                            }

                            break;
                        }

                        var ultimateDestinationClient = ultimateDestinationClients.GetConsumingEnumerable().First();
                        Debug.WriteLine($"Accepted connection from: {ultimateDestinationClient.Client.RemoteEndPoint}");

                        return new
                        {
                            OriginClient = originClient,
                            UltimateDestinationClient = ultimateDestinationClient
                        };
                    })
                    .ToList()
                    .AsParallel()
                    .WithDegreeOfParallelism(connections)
                    .ForAll(pair =>
                    {
                        var toSend = new byte[bytesToSend];
                        var random = new Random();
                        random.NextBytes(toSend);

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

                            Task.WaitAll(testTasks);
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
            catch(Exception ex)
            {
                Debug.WriteLine(ex);
                throw;
            }

            cancelationToken.Cancel();
            ultimateDestination.Stop();
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
        NFS
    }

    public class Side
    {
        public Side(OS os, ProcessRunner runner, string args)
        {
            OS = os;
            Runner = runner;
            Args = args;
        }

        public OS OS { get; }
        public ProcessRunner Runner { get; }
        public string Args { get; }
    }

    public class Server
    {
        public Server(OS OS, FileShareType fileShareType)
        {
            this.OS = OS;
            FileShareType = fileShareType;
        }

        public OS OS { get; }
        public FileShareType FileShareType { get; }
    }
}
