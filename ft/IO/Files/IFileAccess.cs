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

        void WriteAllBytes(string path, byte[] bytes, bool overwrite = true);

        void Move(string sourceFileName, string destFileName, bool overwrite);

        byte[] ReadAllBytes(string path);

        long GetFileSize(string path);
    }
}
