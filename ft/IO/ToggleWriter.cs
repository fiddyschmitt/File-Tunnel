using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleWriter(BinaryWriter writer, long position)
    {
        readonly BinaryWriter Writer = writer;
        readonly long Position = position;

        public void Set(byte value)
        {
            Writer.BaseStream.Seek(Position, SeekOrigin.Begin);
            Writer.Write(value);
            Writer.Flush();
        }

        public void Set(long value)
        {
            Writer.BaseStream.Seek(Position, SeekOrigin.Begin);
            Writer.Write(value);
            Writer.Flush();
        }
    }
}
