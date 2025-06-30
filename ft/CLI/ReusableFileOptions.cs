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
        public const int DEFAULT_MAX_SIZE_BYTES = 10 * 1024 * 1024;

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can grow to before restarting. Default 10485760 (10 MB)")]
        public int MaxFileSizeBytes { get; set; } = DEFAULT_MAX_SIZE_BYTES;

        [Option("isolated-reads", Required = false, HelpText = @"For read operations, the file is opened, read and closed in quick succession. This significantly reduces the tunnel responsiveness.")]
        public bool IsolatedReads { get; set; } = false;

        [Option("upload-download", Required = false, HelpText = @"In this mode, the program will write to a file then wait for it to be deleted by the counterpart (signaling it was processed).")]
        public bool UploadDownload { get; set; } = false;
    }
}
