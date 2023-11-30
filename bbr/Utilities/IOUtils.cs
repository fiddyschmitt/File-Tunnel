using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace bbrelay.Utilities
{
    public static class IOUtils
    {
        [DllImport("Shlwapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private extern static bool PathFileExists(StringBuilder path);

        /*
        public static bool FileExists(string filename)
        {
            // A StringBuilder is required for interops calls that use strings
            var builder = new StringBuilder();
            builder.Append(filename);
            bool exists = PathFileExists(builder);
            return exists;
        }
        */

        /*
        public static bool FileExists(string filename, int timeoutMillis = 100)
        {
            var task = new Task<bool>(() =>
            {
                var fi = new FileInfo(filename);
                return fi.Exists;
            });
            task.Start();
            return task.Wait(timeoutMillis) && task.Result;
        }
        */

        public static bool FileExists(string filename)
        {
            return File.Exists(filename);
        }
    }
}
