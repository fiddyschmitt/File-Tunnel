using System;
using System.IO;
using System.Threading.Tasks;

namespace ft.IO.Files;

public interface IFileAccess
{
    Task<bool> ExistsAsync(string path);

    Task DeleteAsync(string path);

    Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true);

    Task MoveAsync(string sourceFileName, string destFileName, bool overwrite);

    Task<Stream> GetStreamAsync(string path);

    Task<long> GetFileSizeAsync(string path);
}