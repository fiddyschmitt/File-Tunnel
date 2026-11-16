using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ft.Streams
{
    // The counterpart's tunnel file, opened for reading on a Mac's SMB mount (smbfs), with the unified buffer cache
    // and read-ahead turned off for its handle, so that every read is answered by the server.
    //
    // A cached smbfs read handle is served a stale view of a file that another host writes to. ForceRead used to fix
    // that with a second handle, opened, read with F_NOCACHE and closed again for each refresh, every 1-5 ms while a
    // reader waited, and the opening was the trouble: with both ends of a tunnel on one Mac, on a Windows server's
    // share, one ft opening and closing the file while the other extended it made smbfs lose some of the other's
    // writes. The server's copy had zeros there, though it had everything written after, so the reader waited for
    // data that never came and the tunnel went offline. Measured with a writer process and a reader process on one
    // Mac, on a Windows 11 share: 88 of 9,540 64 KB writes lost when the reader opened a handle for each poll, none
    // when it read through handles it kept open, and none on Samba or macOS servers either way. Reading uncached
    // through the one handle needs no refresh at all, and sees a Linux writer's appends within 5 ms at the 90th
    // percentile on Windows, Samba and macOS servers alike.
    //
    // Its own read buffer still has to be dropped, which ForceRead does for every file stream (see
    // Extensions.DropReadBuffer). A peek that met a zero byte, which a reader takes for "not written yet", left what
    // it read in the buffer, and the next peek at the same position was answered from the buffer without reading the
    // file again: the reader waited, until the tunnel timed out, for data the file already held.
    public sealed class MacSmbReadFileStream : FileStream
    {
        const int F_RDAHEAD = 45;
        const int F_NOCACHE = 48;

        public MacSmbReadFileStream(string path, int bufferSize = 4096, FileOptions options = FileOptions.None)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, options)
        {
            var fd = (int)SafeFileHandle.DangerousGetHandle();
            Extensions.MacFcntl(fd, F_RDAHEAD, 0);
            Extensions.MacFcntl(fd, F_NOCACHE, 1);
        }
    }
}
