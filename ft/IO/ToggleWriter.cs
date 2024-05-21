using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleWriter
    {
        readonly BinaryReader Reader;
        readonly BinaryWriter Writer;
        readonly long Position;

        public ToggleWriter(BinaryReader reader, BinaryWriter writer, long position)
        {
            Reader = reader;
            Writer = writer;
            Position = position;
        }

        public void Toggle()
        {
            lock (Reader.BaseStream)
            {
                Reader.BaseStream.Seek(Position, SeekOrigin.Begin);
                var originalValue = Reader.PeekChar();

                var newValue = originalValue == 0 ? (byte)1 : (byte)0;

                Writer.Write(newValue);
                Writer.Flush();
            }
        }
    }
}
