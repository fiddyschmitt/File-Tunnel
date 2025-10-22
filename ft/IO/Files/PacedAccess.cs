using ft.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.IO.Files
{
    public class PacedAccess : IFileAccess
    {
        private readonly int paceMilliseconds;

        public PacedAccess(IFileAccess baseAccess, int paceMilliseconds)
        {
            BaseAccess = baseAccess;
            this.paceMilliseconds = paceMilliseconds;
        }

        public IFileAccess BaseAccess { get; }

        public void Delete(string path)
        {
            Delay.Wait(paceMilliseconds);

            BaseAccess.Delete(path);
        }

        public bool Exists(string path)
        {
            Delay.Wait(paceMilliseconds);

            var result = BaseAccess.Exists(path);
            return result;
        }

        public long GetFileSize(string path)
        {
            Delay.Wait(paceMilliseconds);

            var result = BaseAccess.GetFileSize(path);
            return result;
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            Delay.Wait(paceMilliseconds);

            BaseAccess.Move(sourceFileName, destFileName, overwrite);
        }

        public byte[] ReadAllBytes(string path)
        {
            Delay.Wait(paceMilliseconds);

            var result = BaseAccess.ReadAllBytes(path);
            return result;
        }

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            Delay.Wait(paceMilliseconds);

            BaseAccess.WriteAllBytes(path, bytes, overwrite);
        }
    }
}
