using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class HashingStream(Stream stream) : Stream
    {
        readonly Crc32 crc32 = new();

        bool hashing = false;
        public void StartHashing()
        {
            crc32.Reset();
            hashing = true;
        }

        public void StopHashing()
        {
            hashing = false;
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
            stream.Flush();
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
    }
}
