using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ft.Streams
{
    // Shared bounded EPERM-retry for the isolated (no-held-handle) streams.
    internal static class IsolatedFileIo
    {
        // Run a whole open->op->close as one unit, retrying the transient "access denied" (EPERM) a
        // Win32-OpenSSH sftp server returns when another handle is momentarily open. Its files open
        // exclusively - a held handle admits no second concurrent open, from any client - so a brief
        // write-open/read-open collides. The collision can surface at the open OR at the subsequent
        // read/write/close, so the ENTIRE operation is retried, not just the open (a re-run seeks to the
        // same Position and rewrites the same bytes, so it is idempotent). Bounded by the tunnel timeout,
        // after which a genuine permission error propagates (and the pump restarts). On every other
        // filesystem a concurrent open just succeeds, so this never catches and the retry is free.
        public static void WithRetry(int retryTimeoutMilliseconds, Action operation)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    operation();
                    return;
                }
                catch (UnauthorizedAccessException) when (stopwatch.ElapsedMilliseconds < retryTimeoutMilliseconds)
                {
                    Thread.Sleep(2);
                }
                catch (IOException) when (stopwatch.ElapsedMilliseconds < retryTimeoutMilliseconds)
                {
                    // Some EPERM variants surface as a bare IOException ("Operation not permitted") rather
                    // than UnauthorizedAccessException, particularly from a close/flush. Same transient cause.
                    Thread.Sleep(2);
                }
            }
        }
    }

    // Write analog of IsolatedReadsFileStream: never holds a handle open. Each Write/SetLength opens the
    // file, does the op and closes it (WriteThrough + the close flush push the bytes to the server), so a
    // counterpart can read the file between our writes. This is what lets ft tunnel over a server whose files
    // open exclusively (a held write handle otherwise blocks every other open, even from another client -
    // e.g. the Win32-OpenSSH sftp server behind an sshfs mount). Position is tracked in memory, mirroring the
    // held FileStream it replaces.
    public class IsolatedWritesFileStream : Stream
    {
        public string Filename { get; }
        readonly int retryTimeoutMilliseconds;

        public IsolatedWritesFileStream(string filename, int retryTimeoutMilliseconds)
        {
            Filename = filename;
            this.retryTimeoutMilliseconds = retryTimeoutMilliseconds;
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Position { get; set; }

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

        public override void SetLength(long value)
        {
            IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
            {
                using var fileStream = new FileStream(Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.WriteThrough);
                fileStream.SetLength(value);
            });
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
            {
                using var fileStream = new FileStream(Filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.WriteThrough);
                fileStream.Seek(Position, SeekOrigin.Begin);
                fileStream.Write(buffer, offset, count);
            });
            Position += count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => Position
            };
            return Position;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
