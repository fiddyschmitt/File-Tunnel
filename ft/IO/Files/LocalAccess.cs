using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.IO.Files
{
    public class LocalAccess : IFileAccess
    {
        public LocalAccess()
        {

        }

        public void Delete(string path)
        {
            File.Delete(path);
        }

        public bool Exists(string path)
        {
            //var result = File.Exists(path);

            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) folder = AppDomain.CurrentDomain.BaseDirectory;
            var filename = Path.GetFileName(path);

            //Enumerating files followed by checking if file exists seems to be the most stable way to check if a file exists (for SMB)
            var result = Directory.EnumerateFiles(folder, filename, SearchOption.TopDirectoryOnly).Any() &&
                            File.Exists(path);

            return result;
        }
        public long GetFileSize(string path)
        {
            var result = new FileInfo(path).Length;

            return result;
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            File.Move(sourceFileName, destFileName, overwrite);
        }

        public byte[] ReadAllBytes(string path)
        {
            var result = File.ReadAllBytes(path);

            return result;
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
        }
    }
}