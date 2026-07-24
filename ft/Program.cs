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
        const string VERSION = "3.4.0";

        public const int UNIVERSAL_TIMEOUT_MS = 4000;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ReusableFileOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(FtpOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WebDavOptions))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(S3Options))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DropboxOptions))]
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
            else if (args.Contains("--webdav"))
            {
                parser
                    .ParseArguments<WebDavOptions>(args)
                    .WithParsed(RunWebDavSession)
                    .WithNotParsed(err => Environment.Exit(1));
            }
            else if (args.Contains("--s3"))
            {
                parser
                    .ParseArguments<S3Options>(args)
                    .WithParsed(RunS3Session)
                    .WithNotParsed(err => Environment.Exit(1));
            }
            else if (args.Contains("--dropbox"))
            {
                parser
                    .ParseArguments<DropboxOptions>(args)
                    .WithParsed(RunDropboxSession)
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

        //Every read poll against an HTTP backend is a billable and/or rate-limited API request, and the
        //blocking reader retries an absent slot as fast as the pace allows - at the 1ms floor that's
        //~270 req/s per side while idle against a low-latency endpoint (measured against nginx WebDAV
        //on a LAN). Internet RTT self-limits the loop, which is why this doesn't show up in WAN use.
        //Default to a gentler cadence when the user hasn't chosen one; an explicit --pace still wins.
        //
        //The tunnel timeout also needs headroom on high-RTT services: it bounds the whole
        //write->notice->read->delete slot cycle (an unacknowledged slot blocks the single send pump
        //for up to the full timeout, and DefaultSleepStrategy cancels any file operation that exceeds
        //it), so the 10s default tears the tunnel down on slow S3/WebDAV endpoints. 60s is the
        //compromise, matching the Dropbox tuning: much higher and a stalled pump starves the
        //keep-alive pings for that much longer, so the tunnel never comes online. Confirmed reliable
        //by user testing against a slow S3-compatible server.
        private static void ApplyHttpBackendTuning(string backendName)
        {
            if (Options.PaceMilliseconds == 0)
            {
                Options.PaceMilliseconds = 50;
                Log($"Applying {backendName} tuning: {Options.PaceMilliseconds}ms pace between requests. Override with --pace.", ConsoleColor.Yellow);
            }

            const int recommendedTunnelTimeoutMillis = 60000;
            if (Options.TunnelTimeoutMilliseconds == Options.DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS)
            {
                Options.TunnelTimeoutMilliseconds = recommendedTunnelTimeoutMillis;
                Log($"Applying {backendName} tuning: {Options.TunnelTimeoutMilliseconds:N0}ms tunnel timeout (high-RTT services need headroom for each upload/download cycle). Override with --tunnel-timeout.", ConsoleColor.Yellow);
            }
            else if (Options.TunnelTimeoutMilliseconds < recommendedTunnelTimeoutMillis)
            {
                Log($"Warning: {backendName} endpoints can be slow. If the tunnel drops out, try --tunnel-timeout {recommendedTunnelTimeoutMillis} or higher.", ConsoleColor.Yellow);
            }
        }

        private static void RunWebDavSession(WebDavOptions o)
        {
            ApplyHttpBackendTuning("WebDAV");

            var access = new WebDav(o.WebDavUrl, o.ResolveUsername(), o.ResolvePassword());

            var sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             Options.TunnelTimeoutMilliseconds,
                                             1,
                                             0,        //remote HTTP transport: no batch cap (like FTP) - whole-object PUT/GET has no torn-read risk, so fewer/larger files = fewer round-trips
                                             true,     //blocking reader: strict ping-pong over one shared HttpClient (locked), so idle on the read slot to keep the tunnel online without a poll-storm
                                             o.Verbose);

            RunSession(sharedFileManager, o, o.MaxFileSizeBytes);
        }

        private static void RunS3Session(S3Options o)
        {
            var accessKey = o.ResolveAccessKey();
            var secretKey = o.ResolveSecretKey();

            if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            {
                Log("An S3 access key and secret key are required. Provide them via --access-key / --secret-key, or via the FT_S3_ACCESS_KEY / FT_S3_SECRET_KEY environment variables.", ConsoleColor.Red);
                Environment.Exit(1);
                return;
            }

            ApplyHttpBackendTuning("S3");

            var access = new S3(o.Endpoint, o.Region, o.Bucket, accessKey, secretKey, o.MaxConnections);

            var sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             Options.TunnelTimeoutMilliseconds,
                                             1,
                                             0,        //remote HTTP transport: no batch cap (like FTP) - whole-object PUT/GET has no torn-read risk, so fewer/larger files = fewer round-trips
                                             true,     //blocking reader: strict ping-pong keeps the tunnel online without hammering the endpoint with 404 polls
                                             o.Verbose);

            RunSession(sharedFileManager, o, o.MaxFileSizeBytes);
        }

        private static void RunDropboxSession(DropboxOptions o)
        {
            var appKey = o.ResolveAppKey();
            var appSecret = o.ResolveAppSecret();
            var refreshToken = o.ResolveRefreshToken();

            if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(refreshToken))
            {
                Log("Dropbox requires an app key, app secret and refresh token. Provide them via --app-key / --app-secret / --refresh-token, or via the FT_DROPBOX_APP_KEY / FT_DROPBOX_APP_SECRET / FT_DROPBOX_REFRESH_TOKEN environment variables. See the wiki for how to obtain a refresh token.", ConsoleColor.Red);
                Environment.Exit(1);
                return;
            }

            //Tune before constructing the client - it reads the (raised) tunnel timeout as its HTTP timeout.
            ApplyDropboxTuning();

            //The Dropbox client authenticates on construction, so bad credentials surface here. Report
            //them cleanly rather than as an unhandled exception.
            Dropbox access;
            try
            {
                access = new Dropbox(appKey, appSecret, refreshToken);
            }
            catch (Exception ex)
            {
                Log($"Could not authenticate with Dropbox: {ex.Message}", ConsoleColor.Red);
                Environment.Exit(1);
                return;
            }

            var sharedFileManager = new UploadDownload(
                                             access,
                                             o.ReadFrom.Trim(),
                                             o.WriteTo.Trim(),
                                             Options.TunnelTimeoutMilliseconds,
                                             1,
                                             0,        //remote HTTP transport: no batch cap (like FTP/S3) - whole-object PUT/GET has no torn-read risk
                                             true,     //blocking reader: strict ping-pong over one shared HttpClient, so idle on the read slot rather than poll-storm
                                             o.Verbose);

            RunSession(sharedFileManager, o, o.MaxFileSizeBytes);
        }

        //Empirically tuned against real Dropbox (joint pace x write-interval sweeps, 2026-07). The two
        //interact, so they were optimised together rather than one-at-a-time:
        //  - pace 30ms: how often the reader polls for the counterpart's upload. Lower finds new data
        //    sooner (interactive round-trips ~3.7s at 30ms vs ~4.5s at 100ms); below ~30ms hits Dropbox's
        //    per-request latency floor with no further gain, and Dropbox does not rate-limit even ~100/s.
        //  - write-interval 1000ms: lets commands batch into fewer, larger uploads. At this fast pace the
        //    reader keeps up, so a LOW write-interval wins on both latency and bandwidth (~97 KB/s for a
        //    2MB round-trip); high values (4000+) make the writer wait to batch, which the slow-pace
        //    default used to want but which now just adds latency and also lags rapid interactive input.
        //  - tunnel-timeout 60000ms: Dropbox's per-op latency needs headroom or the tunnel tears down.
        //Explicit --pace / --write-interval / --tunnel-timeout still win.
        private static void ApplyDropboxTuning()
        {
            if (Options.PaceMilliseconds == 0)
            {
                Options.PaceMilliseconds = 30;
                Log($"Applying Dropbox tuning: {Options.PaceMilliseconds}ms pace between requests. Override with --pace.", ConsoleColor.Yellow);
            }

            if (Options.WriteIntervalMilliseconds == 0)
            {
                Options.WriteIntervalMilliseconds = 1000;
                Log($"Applying Dropbox tuning: {Options.WriteIntervalMilliseconds:N0}ms write interval (batches commands into fewer uploads). Override with --write-interval.", ConsoleColor.Yellow);
            }

            const int recommendedTunnelTimeoutMillis = 60000;
            if (Options.TunnelTimeoutMilliseconds == Options.DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS)
            {
                Options.TunnelTimeoutMilliseconds = recommendedTunnelTimeoutMillis;
                Log($"Applying Dropbox tuning: {Options.TunnelTimeoutMilliseconds:N0}ms tunnel timeout (Dropbox latency needs headroom). Override with --tunnel-timeout.", ConsoleColor.Yellow);
            }
            else if (Options.TunnelTimeoutMilliseconds < recommendedTunnelTimeoutMillis)
            {
                Log($"Warning: Dropbox has high latency. If the tunnel drops out, try --tunnel-timeout {recommendedTunnelTimeoutMillis} or higher.", ConsoleColor.Yellow);
            }
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

            // Auto-select the read mode from the filesystem when the user hasn't requested one, and warn
            // (but honour their choice) if they requested a mode the fs doesn't suit. The per-filesystem
            // knowledge lives in one place: Extensions.ModesForReadFile. (The remote transports - FTP,
            // WebDAV, S3, Dropbox - never reach here; they are dispatched to their own sessions in Main.)
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
                //mount's fs type (statfs) rather than the --upload-download flag - a user could pass
                //--upload-download for some other file share where these 9p-specific tweaks are wrong.
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
            localListeners.Add("socks", o.LocalDynamicForwards, false);

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
