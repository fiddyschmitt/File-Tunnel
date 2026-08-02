using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO.Files
{
    public interface IFileAccess
    {
        bool Exists(string path);

        void Delete(string path);

        //Takes ReadOnlyMemory rather than byte[] so callers can hand over a slice of a buffer they
        //already hold instead of allocating an exact-size copy of every command. Memory rather than
        //Span because the HTTP backends wrap it in ReadOnlyMemoryContent, which has to store it.
        void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes, bool overwrite = true);

        void Move(string sourceFileName, string destFileName, bool overwrite);

        byte[] ReadAllBytes(string path);

        //Reads up to count bytes starting at offset, returning fewer if the file ends first (and an
        //empty array if offset is at or past the end). Backends whose transport supports ranged reads
        //override this to fetch only that slice - which matters because the callers only ever want the
        //16-byte file header, and on the single-subfile transports (FTP/WebDAV/S3/Dropbox) the header
        //shares a file with the whole command payload.
        //
        //The default reads the whole file and slices, so a backend that cannot do ranges still behaves
        //correctly - just no cheaper than before.
        byte[] ReadBytes(string path, long offset, int count)
        {
            return SliceRange(ReadAllBytes(path), servedRange: false, offset, count);
        }

        //Shared by the ranged-read implementations. When the transport served the range (HTTP 206) the
        //bytes already start at offset; otherwise - a whole-file read, or a server that ignored the Range
        //header and answered 200 - slice locally rather than trusting the response to be what we asked
        //for. Returning the wrong bytes here would silently corrupt session-change detection.
        static byte[] SliceRange(byte[] received, bool servedRange, long offset, int count)
        {
            if (servedRange)
            {
                return received.Length <= count ? received : received.AsSpan(0, count).ToArray();
            }

            if (offset >= received.Length) return [];

            var available = (int)Math.Min(count, received.Length - offset);
            return received.AsSpan((int)offset, available).ToArray();
        }

        long GetFileSize(string path);
    }
}
