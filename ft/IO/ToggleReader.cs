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
        int lastReadValue;

        public ToggleReader(BinaryReader reader, long position)
        {
            Reader = reader;
            Position = position;

            Reader.BaseStream.Seek(Position, SeekOrigin.Begin);
            lastReadValue = Reader.PeekChar();
        }

        public void Wait()
        {
            Reader.BaseStream.Seek(Position, SeekOrigin.Begin);

            int currentValue;
            while (true)
            {
                currentValue = Reader.PeekChar();

                if (currentValue != lastReadValue)
                {
                    break;
                }

                Delay.Wait(1);
            }

            lastReadValue = currentValue;
        }
    }
}
