using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO
{
    public class ToggleReader : IDisposable
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

        //One look at the flag: true when it holds the value. When it doesn't, the view of the file is refreshed, so
        //that the next look sees a change.
        public bool Is(byte value)
        {
            reader.BaseStream.Seek(position, SeekOrigin.Begin);

            if (reader.PeekChar() == value)
            {
                return true;
            }

            //required for VirtualBoxSharedFolder,Normal,Windows,Windows,Linux
            reader.BaseStream.ForceRead(tunnelTimeoutMilliseconds, verbose);
            return false;
        }

        public void Wait(byte value)
        {
            while (!Is(value))
            {
                Delay.Wait(1);
            }
        }

        //Closes the handle the flag is read through.
        public void Dispose() => reader.Dispose();
    }
}
