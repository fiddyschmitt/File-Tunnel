using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class IsolatedReadsFileStream : Stream
    {
        public IsolatedReadsFileStream(string filename)
        {
            Filename = filename;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return fileStream.Length;
            }
        }

        public override long Position { get; set; }
        public string Filename { get; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            using var fileStream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fileStream.Seek(Position, SeekOrigin.Begin);

            var read = fileStream.Read(buffer, offset, count);

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
