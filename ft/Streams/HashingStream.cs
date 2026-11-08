using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class HashingStream : Stream
    {
        readonly Crc32 crc32 = new();
        private readonly Stream stream;
        private readonly bool verbose;
        private readonly int tunnelTimeoutMilliseconds;

        bool hashing = false;

        public HashingStream(Stream stream, bool verbose, int tunnelTimeoutMilliseconds)
        {
            this.stream = stream;
            this.verbose = verbose;
            this.tunnelTimeoutMilliseconds = tunnelTimeoutMilliseconds;
        }

        public void StartHashing()
        {
            Reset();
            hashing = true;
        }

        public void StopHashing()
        {
            hashing = false;
        }

        public void Reset()
        {
            crc32.Reset();
        }

        public uint GetCrc32()
        {
            var result = crc32.GetCurrentHashAsUInt32();
            return result;
        }

        public override bool CanRead => stream.CanRead;

        public override bool CanSeek => stream.CanSeek;

        public override bool CanWrite => stream.CanWrite;

        public override long Length => stream.Length;

        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }

        public override void Flush()
        {
            stream.Flush(verbose, tunnelTimeoutMilliseconds);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = stream.Read(buffer, offset, count);

            if (hashing)
            {
                var readBytes = new ReadOnlySpan<byte>(buffer, offset, result);
                crc32.Append(readBytes);
            }

            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var result = stream.Seek(offset, origin);
            return result;
        }

        public override void SetLength(long value)
        {
            stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            stream.Write(buffer, offset, count);

            if (hashing)
            {
                var readBytes = new ReadOnlySpan<byte>(buffer, offset, count);
                crc32.Append(readBytes);
            }
        }

        //Stream's own span and single-byte overloads detour through a rented or newly allocated array and a
        //copy, and this stream sits on ft's hottest paths: BinaryWriter.Write(ReadOnlySpan) lands on the span
        //Write for every command the SendPump writes to its file, and BinaryReader's PeekChar and ReadByte on
        //the single-byte Read for every poll of the read file. Pass them straight to the inner stream instead.
        public override int Read(Span<byte> buffer)
        {
            var result = stream.Read(buffer);

            if (hashing)
            {
                crc32.Append(buffer[..result]);
            }

            return result;
        }

        public override int ReadByte()
        {
            var result = stream.ReadByte();

            if (hashing && result != -1)
            {
                crc32.Append([(byte)result]);
            }

            return result;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            stream.Write(buffer);

            if (hashing)
            {
                crc32.Append(buffer);
            }
        }

        public override void WriteByte(byte value)
        {
            stream.WriteByte(value);

            if (hashing)
            {
                crc32.Append([value]);
            }
        }
    }
}
