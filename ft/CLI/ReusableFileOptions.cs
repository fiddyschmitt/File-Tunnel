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

        [Option("isolated-io", Required = false, HelpText = @"Every read AND write opens, operates on, and closes the file in quick succession, never holding a handle. Needed for filesystems that serve a stale view to a held handle, or that refuse a second concurrent open while a handle is held (e.g. the Win32-OpenSSH sftp server behind an sshfs mount). Reduces throughput.")]
        public bool IsolatedIo { get; set; } = false;

        // Deprecated alias for --isolated-io (which now also isolates writes). Hidden; merged into IsolatedIo at startup.
        [Option("isolated-reads", Required = false, Hidden = true, HelpText = @"Deprecated alias for --isolated-io.")]
        public bool IsolatedReadsLegacy { get; set; } = false;

        [Option("upload-download", Required = false, HelpText = @"In this mode, the program will write to a file then wait for it to be deleted by the counterpart (signaling it was processed).")]
        public bool UploadDownload { get; set; } = false;

        [Option("normal", Required = false, HelpText = @"Force Normal (held-handle) mode, overriding the filesystem auto-detection. Highest performance, but requires a filesystem that refreshes a held read handle (most do; sshfs/vboxsf/virtio-fs may not, or may be slower).")]
        public bool Normal { get; set; } = false;
    }
}
