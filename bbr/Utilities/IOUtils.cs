using bbr;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

        public static IEnumerable<string> Tail(string filename)
        {
            File.Delete(filename);

            while (!IOUtils.FileExists(filename))
            {
                Program.Log($"Waiting for file to be created: {filename}");
                Thread.Sleep(1000);
            }

            var streamReader = new StreamReader(filename, new FileStreamOptions()
            {
                Mode = FileMode.Open,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete
            });

            while (true)
            {
                var lineLengthStr = streamReader.ReadLine();

                if (string.IsNullOrEmpty(lineLengthStr))
                {
                    Delay.Wait(1);
                    continue;
                }

                var lineLength = int.Parse(lineLengthStr);

                string line = "";
                do
                {
                    line += streamReader.ReadLine();
                } while (line.Length < lineLength);

                if (line.StartsWith("$purge"))
                {
                    Program.Log($"Was asked to purge {filename}");

                    //let's truncate the file, so that it doesn't get too big and to signify to the other side that we've processed it.
                    //FPS 30/11/2023: Occasionally, this doesn't seem to clear the file

                    /*
                    var readingFromFile = streamReader.BaseStream as FileStream;
                    readingFromFile.Position = 0;
                    readingFromFile.SetLength(0);
                    readingFromFile.Flush(true);
                    streamReader.DiscardBufferedData();
                    */

                    //streamReader.BaseStream.SetLength(0);
                    //streamReader.Close();

                    streamReader.Close();
                    using (var fs = new FileStream(filename, new FileStreamOptions()
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.ReadWrite | FileShare.Delete
                    }))
                    {
                        fs.SetLength(0);
                    }

                    streamReader = new StreamReader(filename, new FileStreamOptions()
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.ReadWrite | FileShare.Delete
                    });

                    Program.Log($"Purge complete: {filename}");
                }
                else
                {
                    //Program.Log(line.Substring(0, Math.Min(100, line.Length)));
                    yield return line;
                }
            }
        }
    }
}
