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
            var result = File.Exists(path);
            return result;
        }

        //public bool Exists(string path)
        //{
        //    bool result;

        //    try
        //    {
        //        //Still true? Try just File.Exists

        //        //Tuned for SMB Windows-Windows-Window
        //        //This exact combination of confirming a file exists is the only one that doesn't drop the tunnel due to timeout.

        //        var folder = Path.GetDirectoryName(path);
        //        if (string.IsNullOrEmpty(folder)) folder = AppDomain.CurrentDomain.BaseDirectory;
        //        var filename = Path.GetFileName(path);

        //        //Enumerating files followed by checking if file exists seems to be the most stable way to check if a file exists (for SMB)
        //        result = Directory.EnumerateFiles(folder, filename, SearchOption.TopDirectoryOnly).Any();

        //        if (result)
        //        {
        //            //Wasteful but necessary
        //            var content = File.ReadAllBytes(path);
        //            result &= content.Length > 0;
        //        }

        //    }
        //    catch
        //    {
        //        result = false;
        //    }

        //    return result;
        //}

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

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            if (overwrite)
            {
                File.WriteAllBytes(path, bytes);
            }
            else
            {
                if (Exists(path))
                {
                    throw new Exception($"{path} exists. Will not overwrite.");
                }
                else
                {
                    File.WriteAllBytes(path, bytes);
                }
            }
        }

        //public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        //{
        //    if (overwrite)
        //    {
        //        File.WriteAllBytes(path, bytes);
        //    }
        //    else
        //    {
        //        var fs = File.Open(path, FileMode.CreateNew, FileAccess.Write);     //this will only create the file when there isn't one already there
        //        fs.Write(bytes, 0, bytes.Length);
        //        fs.Close();
        //    }
        //}
    }
}