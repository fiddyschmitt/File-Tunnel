using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    public class ReusableFileOptions : Options
    {
        [Option('p', "purge-size", Required = false, HelpText = @"The size (in bytes) at which the file should be emptied and started anew. Setting this to 0 disables purging, and the file will grow indefinitely. (Default 10485760)")]
        public int PurgeSizeInBytes { get; set; } = 10 * 1024 * 1024;

        [Option("isolated-reads", Required = false, HelpText = @"For read operations, the file is opened, read and closed in quick succession. This significantly reduces the tunnel responsiveness.")]
        public bool IsolatedReads { get; set; } = false;

        [Option("upload-download", Required = false, HelpText = @"In this mode, the program will write to a file then wait for it to be deleted by the counterpart (signaling it was processed).")]
        public bool UploadDownload { get; set; } = false;
    }
}
