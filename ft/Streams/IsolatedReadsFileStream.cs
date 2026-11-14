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

        static void BypassCacheOnMac(SafeHandle handle)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }

            try
            {
                Extensions.MacFcntl((int)handle.DangerousGetHandle(), F_NOCACHE, 1);
            }
            catch
            {
                //Best effort: if it fails the read just falls back to the cached path.
            }
        }

        // Linux vboxsf (VirtualBox shared folders) re-reads a file from the host on open() only when the
        // host's modification time has moved forward since the guest last looked; otherwise it keeps serving
        // the pages it has. That time can move backwards: a truncate is stamped with the WRITER guest's clock
        // (rounded down to the second), every other write with the host's. When the writer's clock runs ahead
        // of the host's - as after the host steps its own clock back, which Windows Time does every few days -
        // a purge's truncate is stamped later than the flag write that follows it, so a reader that looked in
        // between never sees the flag: each end then waits on the other until the tunnel times out. Dropping
        // the cached pages first makes the read come from the host whatever the times say.
        const int POSIX_FADV_DONTNEED = 4;

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_fadvise(int fd, long offset, long len, int advice);

        readonly bool dropCachedPages;

        readonly int retryTimeoutMilliseconds;

        // Read-ahead (readAheadBytes > 0): a Read at or past readAheadFrom that the buffered bytes can't serve opens
        // the file once and reads up to readAheadBytes from Position; the Reads that follow are served from that
        // buffer while they fall inside it. A command is read in half a dozen Reads (its id, packet number,
        // connection id, payload length, payload and CRC), each of which otherwise opens the file again. Only bytes
        // the file actually held are buffered, and the data region of a tunnel file is append-only between purges,
        // so serving them again is exact; a short read at the end of the data just means the next Read goes back to
        // the file. Flush drops the buffer: the pump flushes before it re-reads a torn command, whenever a poll
        // finds no new command, and at a purge, when the file is rewritten from the start. Reads below
        // readAheadFrom (the header: the session id and the purge flags, which change in place) always go to the
        // file. The purge-flag readers take no read-ahead at all.
        readonly int readAheadBytes;
        readonly long readAheadFrom;
        byte[]? readAhead;
        long readAheadStart;
        int readAheadLength;

        public IsolatedReadsFileStream(string filename, int retryTimeoutMilliseconds, int readAheadBytes = 0, long readAheadFrom = 0)
        {
            Filename = filename;
            this.retryTimeoutMilliseconds = retryTimeoutMilliseconds;
            this.readAheadBytes = readAheadBytes;
            this.readAheadFrom = readAheadFrom;
            dropCachedPages = Extensions.IsVboxsfMount(filename);
        }

        void DropCachedPages(SafeHandle handle)
        {
            if (!dropCachedPages)
            {
                return;
            }

            try
            {
                posix_fadvise((int)handle.DangerousGetHandle(), 0, 0, POSIX_FADV_DONTNEED);
            }
            catch
            {
                //Best effort: if it fails the read falls back to the cached path.
            }
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

        // A flush drops the read-ahead, as a FileStream's drops its read buffer.
        public override void Flush() => readAheadLength = 0;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readAheadBytes > 0 && Position >= readAheadFrom)
            {
                if (readAheadLength > 0 && Position >= readAheadStart && Position < readAheadStart + readAheadLength)
                {
                    var served = (int)Math.Min(count, readAheadStart + readAheadLength - Position);
                    Buffer.BlockCopy(readAhead!, (int)(Position - readAheadStart), buffer, offset, served);
                    Position += served;
                    return served;
                }

                var want = Math.Max(count, readAheadBytes);
                if (readAhead == null || readAhead.Length < want)
                {
                    readAhead = new byte[want];
                }

                var start = Position;
                var got = 0;
                IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
                {
                    //an unbuffered handle: one read, straight from the file
                    using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                    BypassCacheOnMac(fileStream.SafeFileHandle);
                    DropCachedPages(fileStream.SafeFileHandle);
                    fileStream.Seek(start, SeekOrigin.Begin);
                    got = fileStream.Read(readAhead, 0, want);
                });

                readAheadStart = start;
                readAheadLength = got;
                var first = Math.Min(count, got);
                Buffer.BlockCopy(readAhead, 0, buffer, offset, first);
                Position += first;
                return first;
            }

            var read = 0;
            IsolatedFileIo.WithRetry(retryTimeoutMilliseconds, () =>
            {
                using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BypassCacheOnMac(fileStream.SafeFileHandle);
                DropCachedPages(fileStream.SafeFileHandle);
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
