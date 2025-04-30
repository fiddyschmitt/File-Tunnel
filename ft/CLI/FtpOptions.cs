using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    //[Verb("--ftp", HelpText = "Use an FTP server for the file tunnel.")]
    public class FtpOptions : Options
    {
        [Option("ftp", Required = false, HelpText = @"Use an FTP server for the file tunnel.")]
        public bool FTP { get; set; } = false;

        [Option('h', "host", Required = true, HelpText = @"The hostname or IP address of the FTP server.")]
        public string FtpHost { get; set; } = "";

        [Option("port", Required = false, HelpText = @"The port number of the FTP server. (Default 21)")]
        public int FtpPort { get; set; } = 21;

        [Option('u', "username", Required = false, HelpText = @"The username with which to log into the FTP server.")]
        public string FtpUsername { get; set; } = "";

        [Option('p', "password", Required = false, HelpText = @"The password to log into the FTP server.")]
        public string FtpPassword { get; set; } = "";

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;
    }
}
