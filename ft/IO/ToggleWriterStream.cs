using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleWriterStream
    {
        readonly FileStream FileStream;
        readonly long Position;
        BinaryWriter Writer;

        public ToggleWriterStream(FileStream fileStream, long position)
        {
            FileStream = fileStream;
            Position = position;
            Writer = new BinaryWriter(fileStream);
        }

        public void Set(byte value)
        {
            Writer.BaseStream.Seek(Position, SeekOrigin.Begin);
            Writer.Write(value);
            Writer.Flush();
            FileStream.Flush(true);
        }
    }
}
