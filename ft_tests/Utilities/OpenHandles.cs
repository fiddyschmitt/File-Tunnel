using System.Runtime.InteropServices;
using System.Text;

namespace ft_tests.Utilities
{
    //How many handles this process has open on a file. Windows only: handle values are multiples of 4, and each value
    //that is a handle on a disk file is asked for that file's path.
    public static class OpenHandles
    {
        const uint FILE_TYPE_DISK = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetFileType(IntPtr file);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint length, uint flags);

        public static int On(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Counts open handles on Windows only");
            }

            //The file's path as the handles report it, from a handle of this method's own, closed before the count.
            string target;
            using (var own = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                target = FinalPath(own.SafeFileHandle.DangerousGetHandle()) ?? throw new IOException($"No final path for {path}");
            }

            var count = 0;
            for (long value = 4; value < 1 << 20; value += 4)
            {
                var handle = (IntPtr)value;
                if (GetFileType(handle) == FILE_TYPE_DISK && string.Equals(FinalPath(handle), target, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        static string? FinalPath(IntPtr handle)
        {
            var path = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            return length > 0 && length < path.Capacity ? path.ToString() : null;
        }
    }
}
