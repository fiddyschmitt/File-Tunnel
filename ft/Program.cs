using ft.CLI;
using ft.Listeners;
using ft.Streams;
using ft.Utilities;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using ft.Tunnels;
using ft.IO.Files;
using System.Diagnostics;

namespace ft
{
    public class Program
    {
        const string PROGRAM_NAME = "File Tunnel";
        const string VERSION = "2.3.0";

        public const int UNIVERSAL_TIMEOUT_MS = 4000;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ReusableFileOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(FtpOptions))]
        public static void Main(string[] args)
        {
            if (args.Contains("--version"))
            {
                Console.WriteLine($"{PROGRAM_NAME} {VERSION}");
                return;
            }

            Log($"{PROGRAM_NAME} {VERSION}");
            if (Debugger.IsAttached)
            {
                Log($"{args.ToString(" ")}");
            }

            var parser = new Parser(settings =>
            {
                settings.AllowMultiInstance = true;
                settings.HelpWriter = Console.Out;
            });

            if (args.Contains("--ftp"))
            {
                parser
                    .ParseArguments<FtpOptions>(args)
                    .WithParsed(RunFtpSession)
                    .WithNotParsed(err => Environment.Exit(1));
            }
            else
            {
                parser
                    .ParseArguments<ReusableFileOptions>(args)
                    .WithParsed(RunReusableFileSession)
                    .WithNotParsed(err => Environment.Exit(1));
            }

            while (true)
            {
                try
                {
                    Delay.Wait(1000);
                }
                catch
                {
                    break;
                }
            }
        }

        private static void RunFtpSession(FtpOptions o)
        {
            var access = new Ftp(o.FtpHost, o.FtpPort, o.FtpUsername, o.FtpPassword);

            var sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             o.MaxFileSizeBytes,
                                             o.TunnelTimeoutMilliseconds,
                                             Options.PaceMilliseconds,
                                             o.Verbose);

            RunSession(sharedFileManager, o, o.MaxFileSizeBytes);
        }

        private static void RunReusableFileSession(ReusableFileOptions o)
        {
            if (Path.GetFullPath(o.ReadFrom).Contains("thinclient_drives") && !o.IsolatedReads)
            {
                Log($"Warning: It appears the Read file is stored in xrdp's Drive Redirection folder.", ConsoleColor.Yellow);
                Log($"This can result in the File Tunnel not achieving synchronisation.", ConsoleColor.Yellow);
                Log($"Recommendation: Run File Tunnel using an extra arg --isolated-reads", ConsoleColor.Yellow);
                Log($"Continuing.", ConsoleColor.Yellow);
            }

            if (Options.Citrix && !o.IsolatedReads)
            {
                Log($"Optimizing for Citrix by changing to isolated-reads mode.", ConsoleColor.Yellow);
                o.IsolatedReads = true;
            }

            if (Options.Citrix && Options.PaceMilliseconds < 400)
            {
                if (!SystemUtils.IsRunningInCitrix())
                {
                    //pace needs to be set just on the client side
                    Log($"Warning: When using --citrix mode, it is recommended to use --pace 400 or higher (for tunnel stability).", ConsoleColor.Yellow);
                }
            }

            if (o.IsolatedReads && o.MaxFileSizeBytes == ReusableFileOptions.DEFAULT_MAX_SIZE_BYTES)
            {
                o.MaxFileSizeBytes = 1024 * 1024;
                Log($"Reduced --max-size from {ReusableFileOptions.DEFAULT_MAX_SIZE_BYTES:N0} to {o.MaxFileSizeBytes:N0} to improve tunnel stability.", ConsoleColor.Yellow);
            }

            var access = new LocalAccess();


            SharedFileManager sharedFileManager;

            if (o.UploadDownload)
            {
                sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             o.MaxFileSizeBytes,
                                             o.TunnelTimeoutMilliseconds,
                                             Options.PaceMilliseconds,
                                             o.Verbose);
            }
            else
            {
                sharedFileManager = new ReusableFile(
                                            o.ReadFrom.Trim(),
                                            o.WriteTo.Trim(),
                                            o.MaxFileSizeBytes,
                                            o.TunnelTimeoutMilliseconds,
                                            o.IsolatedReads,
                                            o.Verbose);
            }

            RunSession(sharedFileManager, o, o.MaxFileSizeBytes);
        }

        private static void RunSession(SharedFileManager sharedFileManager, Options o, long maxFileSizeBytes)
        {
            var localListeners = new MultiServer();
            localListeners.Add("tcp", o.LocalTcpForwards, false);
            localListeners.Add("udp", o.LocalUdpForwards, false);

            var localToRemoteTunnel = new LocalToRemoteTunnel(localListeners, sharedFileManager, maxFileSizeBytes, o.ReadDurationMillis);
            _ = new RemoteToLocalTunnel(
                                             o.RemoteTcpForwards.ToList(),
                                             o.RemoteUdpForwards.ToList(),
                                             sharedFileManager,
                                             localToRemoteTunnel,
                                             o.UdpSendFrom,
                                             maxFileSizeBytes,
                                             o.ReadDurationMillis,
                                             o.TunnelTimeoutMilliseconds);

            sharedFileManager.Start();
        }

        public static readonly ConsoleColor OriginalConsoleColour = Console.ForegroundColor;

        public static readonly object ConsoleOutputLock = new();

        public static void Log(string str, ConsoleColor? color = null)
        {
            lock (ConsoleOutputLock)
            {
                // Change color if specified
                if (color.HasValue)
                {
                    Console.ForegroundColor = color.Value;
                }

                Console.WriteLine($"{DateTime.Now}  {str}");

                // Reset to original color
                Console.ForegroundColor = OriginalConsoleColour;
            }
        }
    }
}
