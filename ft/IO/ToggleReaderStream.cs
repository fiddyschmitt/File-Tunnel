using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleReaderStream
    {
        readonly BinaryReader Reader;
        private readonly Stream readStream;
        readonly long Position;

        public ToggleReaderStream(Stream stream, long position)
        {
            Reader = new BinaryReader(stream, Encoding.ASCII);
            readStream = stream;
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

                //force read
                if (readStream is FileStream fileStream)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        using var tempFs = new FileStream(fileStream.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        tempFs.Read(new byte[4096]);
                    }
                    else
                    {
                        fileStream.Flush();
                    }
                }

                if (currentValue == value)
                {
                    break;
                }

                Delay.Wait(1);
            }
        }
    }
}
