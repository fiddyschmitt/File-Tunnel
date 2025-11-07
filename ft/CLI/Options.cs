using CommandLine.Text;
using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    public abstract class Options
    {
        [Option('L', Required = false, HelpText = @"TCP forwarding. Syntax: [bind_address:]port:host:hostport. Specifies that the given port on the local host is to be forwarded to the given host and port on the remote side. Use forward slashes as separators when using IPV6.")]
        public IEnumerable<string> LocalTcpForwards { get; set; } = [];

        [Option('U', Required = false, HelpText = @"UDP forwarding. Syntax: [bind_address:]port:host:hostport. Specifies that the given port on the local host is to be forwarded to the given host and port on the remote side. Use forward slashes as separators when using IPV6.")]
        public IEnumerable<string> LocalUdpForwards { get; set; } = [];



        [Option('R', Required = false, HelpText = @"Remote TCP forwarding. Syntax: [bind_address:]port:host:hostport. Specifies that the given port on the remote host is to be forwarded to the given host and port on the local side. Use forward slashes as separators when using IPV6.")]
        public IEnumerable<string> RemoteTcpForwards { get; set; } = [];

        [Option("remote-udp-fwd", Required = false, HelpText = @"Remote UDP forwarding. Syntax: [bind_address:]port:host:hostport. Specifies that the given port on the remote host is to be forwarded to the given host and port on the local side. Use forward slashes as separators when using IPV6.")]
        public IEnumerable<string> RemoteUdpForwards { get; set; } = [];



        [Option("read-duration", Required = false, HelpText = @"The duration (in milliseconds) to read data from a TCP connection. Larger values increase throughput (by reducing the number of small writes to file), whereas smaller values improve responsiveness. (Default 50 ms)")]
        public int ReadDurationMillis { get; set; } = 50;

        [Option("udp-send-from", Required = false, HelpText = "A local address which UDP data will be sent from. Example --udp-send-from 192.168.1.1:11000")]
        public string UdpSendFrom { get; set; } = "0.0.0.0";



        [Option('w', "write", Required = true, HelpText = @"Where to write data to. Example: --write ""\\nas\share\1.dat""")]
        public string WriteTo { get; set; } = "";

        [Option('r', "read", Required = true, HelpText = @"Where to read data from. Example: --read ""\\nas\share\2.dat""")]
        public string ReadFrom { get; set; } = "";



        public const int DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS = 10000;

        [Option("tunnel-timeout", Required = false, HelpText = @"The duration (in milliseconds) to wait for responses from the counterpart. If this timeout is reached, the tunnel is considered offline and TCP connections will be closed at this point. (Default 10000 ms)")]
        public static int TunnelTimeoutMilliseconds { get; set; } = DEFAULT_TUNNEL_TIMEOUT_MILLISECONDS;

        [Option("pace", Required = false, HelpText = @"The delay (in milliseconds) between file operations. This can be used to reduce the impact on the file server. Default: 0 (no delay)")]
        public static int PaceMilliseconds { get; set; } = 0;

        [Option("write-interval", Required = false, HelpText = @"How often (in milliseconds) file writes should be performed. Default: 0 (as fast as possible)")]
        public static int WriteIntervalMilliseconds { get; set; } = 0;

        [Option("read-interval", Required = false, HelpText = @"How often (in milliseconds) file reads should be performed. Default: 0 (as fast as possible)")]
        public static int ReadIntervalMilliseconds { get; set; } = 0;




        [Option("verbose", Required = false, HelpText = @"Display more detailed output")]
        public bool Verbose { get; set; } = false;



        [Option("citrix", Required = false, HelpText = @"Optimize the tunnel for Citrix file shares such as \\client\c$")]
        public static bool Citrix { get; set; } = false;

        [Option("s3", Required = false, HelpText = @"Optimize the tunnel for S3 file shares")]
        public static bool S3 { get; set; } = false;

        [Option("dropbox", Required = false, HelpText = @"Optimize the tunnel for Dropbox")]
        public static bool Dropbox { get; set; } = false;
    }
}
