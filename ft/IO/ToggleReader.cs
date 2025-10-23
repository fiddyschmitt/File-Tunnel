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
        readonly BinaryReader reader;
        readonly long position;
        private readonly int tunnelTimeoutMilliseconds;
        private readonly bool verbose;

        public ToggleReader(BinaryReader reader, long position, int tunnelTimeoutMilliseconds, bool verbose)
        {
            this.reader = reader;
            this.position = position;
            this.tunnelTimeoutMilliseconds = tunnelTimeoutMilliseconds;
            this.verbose = verbose;

            reader.BaseStream.Seek(position, SeekOrigin.Begin);
        }

        public void Wait(byte value)
        {
            reader.BaseStream.Seek(position, SeekOrigin.Begin);

            int currentValue;
            while (true)
            {
                currentValue = reader.PeekChar();

                if (currentValue == value)
                {
                    break;
                }

                //required for VirtualBoxSharedFolder,Normal,Windows,Windows,Linux
                reader.BaseStream.ForceRead(tunnelTimeoutMilliseconds, verbose);

                Delay.Wait(1);
            }
        }
    }
}
