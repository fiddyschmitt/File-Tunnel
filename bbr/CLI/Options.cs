﻿using CommandLine.Text;
using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.CLI
{
    public class Options
    {
        [Option("tcp-listen", Required = false, HelpText = "Listen for TCP connections. Example --tcp-listen 127.0.0.1:11000")]
        public string? TcpListenTo { get; set; }

        [Option("tcp-connect", Required = false, HelpText = "Connect to a TCP server. Example --tcp-connect 127.0.0.1:22")]
        public string? TcpConnectTo { get; set; }



        [Option("udp-listen", Required = false, HelpText = "A local address on which to listen for UDP data. Example --udp-listen 127.0.0.1:11000")]
        public string? UdpListenTo { get; set; }

        [Option("udp-send-to", Required = false, HelpText = "Forwards data to a UDP endpoint. Example --udp-send-to 192.168.1.50:12000")]
        public string? UdpSendTo { get; set; }

        [Option("udp-send-from", Required = false, HelpText = "A local address which UDP data will be sent from. Example --uudp-send-from 192.168.1.1:11000")]
        public string? UdpSendFrom { get; set; }



        [Option('w', "write", Required = false, HelpText = @"Where to write data to (file, tcp, udp). Example: --write ""\\nas\share\1.dat"" or --write udp://192.168.1.2:12000")]
        public string? WriteTo { get; set; }

        [Option('r', "read", Required = false, HelpText = @"Where to read data from (file, tcp, udp). Example: --read ""\\nas\share\2.dat"" or --read udp://192.168.1.1:12000")]
        public string? ReadFrom { get; set; }




        [Option('v', "version", Required = false, HelpText = "Print the version and exit.")]
        public bool PrintVersion { get; set; }
    }
}
