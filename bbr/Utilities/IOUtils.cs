using bbr;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace bbrelay.Utilities
{
    public static class IOUtils
    {
        [DllImport("Shlwapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private extern static bool PathFileExists(StringBuilder path);

        public static bool FileIsBlank(string path)
        {
            // bufferSize == 1 used to avoid unnecessary buffer in FileStream
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
            long fileLength = 0;

            int index = 0;
            int count = (int)fileLength;
            byte[] bytes = new byte[count];
            while (count > 0)
            {
                int n = fs.Read(bytes, index, count);

                index += n;
                count -= n;
            }

            var result = bytes.All(b => b == 0);
            return result;
        }

        public static IEnumerable<string> Tail(string filename)
        {
            File.Delete(filename);

            while (!File.Exists(filename))
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

        public static void TruncateFile(string filename)
        {
            var attempt = 1;
            do
            {
                Program.Log($"Truncating file, attempt {attempt++:N0}: {filename}");
                using var fs = new FileStream(filename, new FileStreamOptions()
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete
                });
                fs.SetLength(0);

                //for some reason, it sometimes takes more than one go
            } while (new FileInfo(filename).Length > 0 || !IOUtils.FileIsBlank(filename));
        }
    }
}
