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
            int currentValue;
            while (true)
            {
                lock (Reader.BaseStream)
                {
                    Reader.BaseStream.Seek(Position, SeekOrigin.Begin);
                    currentValue = Reader.PeekChar();
                }

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
