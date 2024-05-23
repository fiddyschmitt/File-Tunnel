using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleWriter(BinaryReader reader, BinaryWriter writer, long position)
    {
        readonly BinaryReader Reader = reader;
        readonly BinaryWriter Writer = writer;
        readonly long Position = position;

        public void Toggle()
        {
            Reader.BaseStream.Seek(Position, SeekOrigin.Begin);
            var originalValue = Reader.PeekChar();

            var newValue = originalValue == 0 ? (byte)1 : (byte)0;

            Writer.Write(newValue);
            Writer.Flush();
        }
    }
}
