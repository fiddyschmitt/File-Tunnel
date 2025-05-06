using ft;
using ft_tests.Runner;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        //static ProcessRunner linux_x64_3;

        public void Setup()
        {
            var config = new ConfigurationBuilder()
                                .AddUserSecrets<EndToEndTests>()
                                .Build();

            win10_x64_1 = new LocalWindowsProcessRunner(WIN_X64_EXE);
            //win10_x64_2 = new WindowsProcessRunner("192.168.1.5", config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE);                 //win10 VM
            win10_x64_3 = new RemoteWindowsProcessRunner("192.168.1.20", config["edm_username"], config["edm_password"], WIN_X64_EXE);        //elitedesk

            linux_x64_1 = new LinuxProcessRunner("192.168.1.80", "user", "live", LINUX_X64_EXE, "/user/home/");
            //linux_x64_2 = new LinuxProcessRunner("192.168.1.81", "user", "live", LINUX_X64_EXE, "/user/home/");
            //linux_x64_3 = new LinuxProcessRunner("192.168.1.82", "user", "live", LINUX_X64_EXE, "/user/home/");

        }

        [TestMethod]
        public void Smb()
        {
            Setup();

            OS[] client1 = [OS.Windows, OS.Linux];
            OS[] servers = [OS.Windows, OS.Linux];
            OS[] client2 = [OS.Windows, OS.Linux];

            client1 = [OS.Windows];
            servers = [OS.Windows];
            client2 = [OS.Linux];


            var combinations = Utilities.Extensions
                                .CartesianProduct([client1, servers, client2])
                                .Select(combo =>
                                {
                                    var lst = combo.ToList();
                                    return new
                                    {
                                        Client1 = lst[0],
                                        Server = lst[1],
                                        Client2 = lst[2]
                                    };
                                })
                                .ToList();

            var pathLookup = (OS client, OS server, string fileName) =>
            {
                var result = "";

                if (client == OS.Windows && server == OS.Windows) result = @$"\\192.168.1.5\shared\{fileName}";
                if (client == OS.Windows && server == OS.Linux) result = @$"\\192.168.1.81\shared\{fileName}";
                if (client == OS.Linux && server == OS.Windows) result = @$"/media/win10_vm/shared/{fileName}";
                if (client == OS.Linux && server == OS.Linux) result = @$"/media/smb/192.168.1.81/shared/{fileName}";

                return result;
            };

            combinations
                .ForEach(combo =>
                {
                    var name = $"({combo.Client1}-{combo.Server}-{combo.Client2})";

                    var client1_process_runner = combo.Client1 switch
                    {
                        OS.Windows => win10_x64_1,
                        OS.Linux => linux_x64_1,
                        _ => throw new NotImplementedException()
                    };

                    var writePath1 = pathLookup(combo.Client1, combo.Server, "1.dat");
                    var readPath1 = pathLookup(combo.Client1, combo.Server, "2.dat");

                    var side1 = new Side(client1_process_runner, $"-w {writePath1} -r {readPath1}");



                    var client2_process_runner = combo.Client2 switch
                    {
                        OS.Windows => win10_x64_3,
                        OS.Linux => linux_x64_1,
                        _ => throw new NotImplementedException()
                    };

                    var readPath2 = pathLookup(combo.Client2, combo.Server, "1.dat");
                    var writePath2 = pathLookup(combo.Client2, combo.Server, "2.dat");

                    var side2 = new Side(client2_process_runner, $"-r {readPath2} -w {writePath2}");

                    ConductTunnelTests(name, side1, side2);
                });
        }

        public static void ConductTunnelTests(string name, Side side1, Side side2)
        {
            ConductTest(
                $"{name} - Normal",
                new Side(side1.Runner, $"{side1.Args} -L 5002:127.0.0.1:5003 -R 5003:127.0.0.1:5004"),
                new Side(side2.Runner, $"{side2.Args}"));

            ConductTest(
                $"{name} - Normal + --isolated-reads",
                new Side(side1.Runner, $"{side1.Args} -L 5002:127.0.0.1:5003 -R 5003:127.0.0.1:5004 --isolated-reads"),
                new Side(side2.Runner, $"{side2.Args}"));

            ConductTest(
                $"{name} - --upload-download",
                new Side(side1.Runner, $"{side1.Args} -L 5002:127.0.0.1:5003 -R 5003:127.0.0.1:5004 --upload-download"),
                new Side(side2.Runner, $"{side2.Args} --upload-download"));
        }

        public static void ConductTest(string name, Side side1, Side side2)
        {
            side1.Runner.Run(side1.Args);
            side2.Runner.Run(side2.Args);

            TestTransfer(5 * 1024 * 1024, true, 10);

            side1.Runner.Stop();
            side2.Runner.Stop();
        }

        public static void TestTransfer(int bytesToSend, bool fullDuplex, int connections)
        {
            var ultimateDestination = new TcpListener("127.0.0.1:5004".AsEndpoint());
            ultimateDestination.Start();
            var ultimateDestinationAcceptCT = new CancellationTokenSource();
            var ultimateDestinationClients = new BlockingCollection<TcpClient>();

            Task.Factory.StartNew(() =>
            {
                while (!ultimateDestinationAcceptCT.IsCancellationRequested)
                {
                    try
                    {
                        var client = ultimateDestination.AcceptTcpClient();
                        ultimateDestinationClients.Add(client);
                    }
                    catch
                    {
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

                    while (true)
                    {
                        var duration = DateTime.Now - startTime;
                        if (duration.TotalSeconds > 22)
                        {
                            throw new Exception("Could not connect");
                        }

                        try
                        {
                            originClient.Connect("127.0.0.1:5002".AsEndpoint());
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


            ultimateDestinationAcceptCT.Cancel();
            ultimateDestination.Stop();
        }

        static void TestDirection(string direction, TcpClient sender, TcpClient receiver, byte[] toSend)
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
            Assert.IsTrue(receivedSuccessfully, $"[{direction}] Received buffer does not match sent buffer");
        }
    }

    public enum OS
    {
        Windows,
        Linux,
        Mac
    }

    public class Side
    {
        public Side(ProcessRunner runner, string args)
        {
            Runner = runner;
            Args = args;
        }

        public ProcessRunner Runner { get; }
        public string Args { get; }
    }
}
