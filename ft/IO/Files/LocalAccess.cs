using System;
using System.IO;
using System.Threading.Tasks;

namespace ft.IO.Files;

public class LocalAccess : IFileAccess
{
    public Task<bool> ExistsAsync(string path)
    {
        return Task.FromResult(File.Exists(path));
    }

    public Task DeleteAsync(string path)
    {
        File.Delete(path);

        return Task.CompletedTask;
    }

    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        if (!overwrite && File.Exists(path))
        {
            throw new Exception($"{path} exists. Will not overwrite.");
        }

        return File.WriteAllBytesAsync(path, buffer);
    }

    public Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        File.Move(sourceFileName, destFileName, overwrite);

        return Task.CompletedTask;
    }

    public Task<Stream> GetStreamAsync(string path)
    {
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<long> GetFileSizeAsync(string path)
    {
        return Task.FromResult(new FileInfo(path).Length);
    }
}