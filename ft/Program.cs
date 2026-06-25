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
        const string VERSION = "3.0.3";

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
                                             Options.TunnelTimeoutMilliseconds,
                                             1,
                                             0,        //no batch cap for FTP: every file is a separate data-connection round-trip on one serialized connection, so fewer/larger files = far less contention (the original behaviour). The 9p cap exists only to avoid torn reads on a coherent local mount.
                                             true,     //FTP: blocking reader - its single serialized FtpClient must stay free for the keep-alive pings (true at any subfile count)
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

            if (Options.Citrix)
            {
                o.IsolatedReads = true;

                if (Options.PaceMilliseconds < 400)
                {
                    //pace needs to be set just on the client side
                    Log($"Warning: When using --citrix mode, if the tunnel drops out regularly try using --pace 400 or higher on the Citrix client end.", ConsoleColor.Yellow);
                }
            }

            if (Options.S3)
            {
                o.UploadDownload = true;
            }

            if (Options.Dropbox)
            {
                o.UploadDownload = true;

                Options.PaceMilliseconds = Math.Max(100, Options.PaceMilliseconds);

                var recommendedWriteIntervalMillis = 4000;
                if (Options.WriteIntervalMilliseconds == 0)
                {
                    //If we write too often, Rclone waits a long time until until it start uploading.
                    //Rclone only starts uploading 1 second after the file is no longer in use. This is controlled by the arg given to rclone (--vfs-write-back 1s).
                    //Also, rclone takes about 3 seconds to upload the file so we have to include that also.
                    Options.WriteIntervalMilliseconds = recommendedWriteIntervalMillis;
                }
                else
                {
                    if (Options.WriteIntervalMilliseconds < recommendedWriteIntervalMillis)
                    {
                        Log($"Warning: Dropbox only supports writing every 4 seconds or longer. Recommend using --write-interval {recommendedWriteIntervalMillis} or higher.", ConsoleColor.Yellow);
                    }
                }

                var recommendedTunnelTimeoutMillis = 60000;
                if (Options.TunnelTimeoutMilliseconds == Options.DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS)
                {
                    //Dropbox latency is anywhere between 12-30 seconds. Let's increase the tunnel timeout
                    Options.TunnelTimeoutMilliseconds = recommendedTunnelTimeoutMillis;
                }
                else
                {
                    if (Options.TunnelTimeoutMilliseconds < recommendedTunnelTimeoutMillis)
                    {
                        Log($"Warning: Dropbox has high latency. Recommend using --tunnel-timeout {recommendedTunnelTimeoutMillis} or higher.", ConsoleColor.Yellow);
                    }
                }
            }

            // Auto-select the read mode from the filesystem when the user hasn't requested one, and warn
            // (but honour their choice) if they requested a mode the fs doesn't suit. The per-filesystem
            // knowledge lives in one place: Extensions.ModesForReadFile. Skipped for the rclone-FUSE
            // transports (S3/Dropbox), which set --upload-download above as part of their own setup.
            if (!Options.S3 && !Options.Dropbox)
            {
                var fs = Extensions.ModesForReadFile(o.ReadFrom);
                Extensions.TunnelMode? requested =
                    o.Normal ? Extensions.TunnelMode.Normal :
                    o.IsolatedReads ? Extensions.TunnelMode.IsolatedReads :
                    o.UploadDownload ? Extensions.TunnelMode.UploadDownload :
                    null;

                if (requested is null)
                {
                    o.IsolatedReads = fs.Preferred == Extensions.TunnelMode.IsolatedReads;
                    o.UploadDownload = fs.Preferred == Extensions.TunnelMode.UploadDownload;
                    if (fs.Description.Length > 0)
                    {
                        Log($"The Read file is on {fs.Description}. Auto-selecting {fs.Preferred.ModeFlag()}.", ConsoleColor.Yellow);
                    }
                }
                else if (fs.Description.Length > 0 && requested.Value != fs.Preferred)
                {
                    var why = fs.Supports(requested.Value)
                        ? $"works there, but {fs.Preferred.ModeFlag()} is recommended (faster)"
                        : $"is not supported there - {fs.Preferred.ModeFlag()} is recommended";
                    Log($"Warning: you specified {requested.Value.ModeFlag()}, but the Read file is on {fs.Description}, which {why}. Continuing with {requested.Value.ModeFlag()}.", ConsoleColor.Yellow);
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
                //The small-file cap and low pace are specific to a real 9p mount, so key them off the
                //mount's fs type (statfs) rather than the --upload-download flag: S3/Dropbox rclone-FUSE
                //mounts also use --upload-download, and these tweaks are wrong for them (Dropbox sets its
                //own pace above and wants large files for its 4s write-back interval).
                if (Extensions.IsNinePMount(o.ReadFrom))
                {
                    //9p needs small files: with large files the reader intermittently catches one mid-write
                    //and truncates (~2/5 runs); a 64KB cap is reliably 5/5. (Confirmed by ablation.)
                    o.MaxFileSizeBytes = 65536;

                    //9p also needs a low pace: the keep-alive ping round-trips through the same subfile
                    //rotation, so a high pace ages it past the offline timeout (pace 100 fails; 10 is
                    //reliable). Default to 10ms when the user hasn't set one.
                    if (Options.PaceMilliseconds == 0)
                    {
                        Options.PaceMilliseconds = 10;
                    }

                    Log($"The Read file is on a 9P mount. Applying 9P tuning: 64KB file cap, {Options.PaceMilliseconds}ms pace.", ConsoleColor.Yellow);
                }

                sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             Options.TunnelTimeoutMilliseconds,
                                             5,    //using multiple subfiles improves latency for remote file systems such as Dropbox and S3
                                             o.MaxFileSizeBytes,
                                             false,   //9p: non-blocking reader - independent files, so skip an absent slot rather than head-of-line stall on it
                                             o.Verbose);
            }
            else
            {
                sharedFileManager = new ReusableFile(
                                            o.ReadFrom.Trim(),
                                            o.WriteTo.Trim(),
                                            o.MaxFileSizeBytes,
                                            Options.TunnelTimeoutMilliseconds,
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
                                             Options.TunnelTimeoutMilliseconds);

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
