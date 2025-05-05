using ft_tests.Runner;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests
{
    [TestClass]
    public class EndToEndTests
    {
        const string WIN_X64_EXE = @"R:\Temp\ft release\ft-win-x64.exe";
        const string LINUX_X64_EXE = @"R:\Temp\ft release\ft-linux-x64.exe";

        static ProcessRunner win10_x64_1;
        static ProcessRunner win10_x64_2;
        static ProcessRunner win10_x64_3;


        static ProcessRunner linux_x64_1;
        static ProcessRunner linux_x64_2;
        static ProcessRunner linux_x64_3;

        public void Setup()
        {
            var config = new ConfigurationBuilder()
                                .AddUserSecrets<EndToEndTests>()
                                .Build();

            win10_x64_1 = new LocalWindowsProcessRunner(WIN_X64_EXE);
            //win10_x64_2 = new WindowsProcessRunner("192.168.1.5", config["win10_vm_username"], config["win10_vm_password"], WIN_X64_EXE);                 //win10 VM
            win10_x64_3 = new RemoteWindowsProcessRunner("192.168.1.20", config["gabrielle"], config["edm_password"], WIN_X64_EXE);        //elitedesk

            linux_x64_1 = new LinuxProcessRunner("192.168.1.80", "user", "live", LINUX_X64_EXE, "/user/home/");
            //linux_x64_2 = new LinuxProcessRunner("192.168.1.81", "user", "live", LINUX_X64_EXE, "/user/home/");
            linux_x64_3 = new LinuxProcessRunner("192.168.1.82", "user", "live", LINUX_X64_EXE, "/user/home/");

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
            client2 = [OS.Windows];


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
                if (client == OS.Linux && server == OS.Windows) result = @$"/media/smb/192.168.1.5/shared/{fileName}";
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
                        OS.Linux => linux_x64_3,
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
                new Side(side1.Runner, $"{side1.Args} -L 5001:192.168.1.20:3389"),
                new Side(side2.Runner, $"{side2.Args}"));

            //reverse

            //normal + --isolated-reads

            //upload-download
        }

        public static void ConductTest(string name, Side side1, Side side2)
        {
            side1.Runner.Run(side1.Args);
            side2.Runner.Run(side2.Args);

            while (true)
            {
                Thread.Sleep(1000);
            }
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
