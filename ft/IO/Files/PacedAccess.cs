using System;
using System.IO;
using System.Threading.Tasks;

namespace ft.IO.Files;

public class PacedAccess : IFileAccess
{
    private readonly int paceMilliseconds;

    public PacedAccess(IFileAccess baseAccess, int paceMilliseconds)
    {
        BaseAccess = baseAccess;
        this.paceMilliseconds = paceMilliseconds;
    }

    private IFileAccess BaseAccess { get; }

    public async Task<bool> ExistsAsync(string path)
    {
        await Task.Delay(paceMilliseconds);
        return await BaseAccess.ExistsAsync(path);
    }

    public async Task DeleteAsync(string path)
    {
        await Task.Delay(paceMilliseconds);
        await BaseAccess.DeleteAsync(path);
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        await Task.Delay(paceMilliseconds);
        await BaseAccess.WriteAllBytesAsync(path, buffer, overwrite);
    }

    public async Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        await Task.Delay(paceMilliseconds);
        await BaseAccess.MoveAsync(sourceFileName, destFileName, overwrite);
    }

    public async Task<Stream> GetStreamAsync(string path)
    {
        await Task.Delay(paceMilliseconds);
        return await BaseAccess.GetStreamAsync(path);
    }

    public async Task<long> GetFileSizeAsync(string path)
    {
        await Task.Delay(paceMilliseconds);
        return await BaseAccess.GetFileSizeAsync(path);
    }
}