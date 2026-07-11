using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    public class WebDavOptions : Options
    {
        [Option("webdav", Required = false, HelpText = @"Use a WebDAV server for the file tunnel.")]
        public bool WebDav { get; set; } = false;

        [Option("url", Required = true, HelpText = @"The base URL of the WebDAV server (folder). Example: --url https://example.com/remote.php/dav/files/user/")]
        public string WebDavUrl { get; set; } = "";

        [Option('u', "username", Required = false, HelpText = @"The username with which to log into the WebDAV server.")]
        public string WebDavUsername { get; set; } = "";

        [Option('p', "password", Required = false, HelpText = @"The password to log into the WebDAV server.")]
        public string WebDavPassword { get; set; } = "";

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;
    }
}
