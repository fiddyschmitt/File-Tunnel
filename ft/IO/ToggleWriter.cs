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
        private readonly BinaryWriter writer;
        private readonly long position;
        private readonly int tunnelTimeoutMilliseconds;
        private readonly bool verbose;

        public ToggleWriter(BinaryWriter writer, long position, int tunnelTimeoutMilliseconds, bool verbose)
        {
            this.writer = writer;
            this.position = position;
            this.tunnelTimeoutMilliseconds = tunnelTimeoutMilliseconds;
            this.verbose = verbose;
        }

        public void Set(byte value)
        {
            writer.BaseStream.Seek(position, SeekOrigin.Begin);

            Extensions.Retry(
                $"{nameof(ToggleWriter)}.{nameof(Set)}() -> {nameof(writer)}.{nameof(writer.Write)}()",
                () => writer.Write(value),
                verbose,
                tunnelTimeoutMilliseconds);

            writer.Flush(verbose, tunnelTimeoutMilliseconds);
        }
    }
}
