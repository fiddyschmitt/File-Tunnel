using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class IsolatedReadsFileStream : Stream
    {
        // macOS: fcntl(fd, F_NOCACHE, 1) turns off the unified buffer cache for this descriptor, so a read
        // must go to the SMB server rather than being served a stale cached page. It is the macOS analog of
        // O_DIRECT on Linux and FILE_FLAG_NO_BUFFERING on Windows, but with no sector-alignment requirement.
        //
        // Reopening alone (what this stream already does) is not enough on macOS: the fresh handle still
        // reads through the buffer cache and is served the same stale bytes - and, worse, a read at a
        // position past the client's cached EOF returns nothing even though the server has the data. An
        // F_NOCACHE read goes to the server and returns those bytes.
        const int F_NOCACHE = 48;

        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int cmd, int arg);

        static void BypassCacheOnMac(SafeHandle handle)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }

            try
            {
                fcntl((int)handle.DangerousGetHandle(), F_NOCACHE, 1);
            }
            catch
            {
                //Best effort: if it fails the read just falls back to the cached path.
            }
        }

        readonly int retryTimeoutMilliseconds;

        public IsolatedReadsFileStream(string filename, int retryTimeoutMilliseconds)
        {
            Filename = filename;
            this.retryTimeoutMilliseconds = retryTimeoutMilliseconds;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                long length = 0;
                IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
                {
                    using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    length = fileStream.Length;
                });
                return length;
            }
        }

        public override long Position { get; set; }
        public string Filename { get; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
            {
                using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BypassCacheOnMac(fileStream.SafeFileHandle);
                fileStream.Seek(Position, SeekOrigin.Begin);
                read = fileStream.Read(buffer, offset, count);
            });

            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;

                case SeekOrigin.Current:
                    Position += offset;
                    break;

                case SeekOrigin.End:
                    Position = Length + offset;
                    break;
            }

            return Position;
        }

        public override void SetLength(long value) => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }
}
