using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO.Files
{
    public class LocalAccess : IFileAccess
    {
        public void Delete(string path)
        {
            File.Delete(path);
        }

        public bool Exists(string path)
        {
            var folder = Path.GetDirectoryName(path) ?? AppDomain.CurrentDomain.BaseDirectory;
            var filename = Path.GetFileName(path);

            //File.Exists() interferes with SMB's operations, and slows things down.
            //The following is less intrusive.
            var result = Directory.EnumerateFiles(folder, filename).Any();
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
