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
        private readonly int operationDelayMillis;

        public LocalAccess(int operationDelayMillis)
        {
            this.operationDelayMillis = operationDelayMillis;
        }

        public void Delete(string path)
        {
            File.Delete(path);

            Thread.Sleep(operationDelayMillis);
        }

        public bool Exists(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) folder = AppDomain.CurrentDomain.BaseDirectory;

            var filename = Path.GetFileName(path);

            //File.Exists() interferes with SMB's operations, and slows things down.
            //The following is less intrusive.
            var result = Directory.EnumerateFiles(folder, filename).Any();

            Thread.Sleep(operationDelayMillis);

            return result;
        }

        public long GetFileSize(string path)
        {
            var result = new FileInfo(path).Length;

            Thread.Sleep(operationDelayMillis);

            return result;
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            File.Move(sourceFileName, destFileName, overwrite);

            Thread.Sleep(operationDelayMillis);
        }

        public byte[] ReadAllBytes(string path)
        {
            var result = File.ReadAllBytes(path);

            Thread.Sleep(operationDelayMillis);

            return result;
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);

            Thread.Sleep(operationDelayMillis);
        }
    }
}
