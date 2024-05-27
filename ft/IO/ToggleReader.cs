using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleReader
    {
        readonly BinaryReader Reader;
        readonly long Position;

        public ToggleReader(BinaryReader reader, long position)
        {
            Reader = reader;
            Position = position;

            Reader.BaseStream.Seek(Position, SeekOrigin.Begin);
        }

        public void Wait(byte value)
        {
            Reader.BaseStream.Seek(Position, SeekOrigin.Begin);

            int currentValue;
            while (true)
            {
                currentValue = Reader.PeekChar();

                if (currentValue == value)
                {
                    break;
                }

                Delay.Wait(1);
            }
        }
    }
}
