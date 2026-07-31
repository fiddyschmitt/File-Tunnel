using FluentFTP;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ft.IO.Files;

public class Ftp : IFileAccess
{
    private readonly FtpClient client;

    public Ftp(string host, int port, string username, string password)
    {
        var config = new FtpConfig
        {
            ConnectTimeout = Program.UNIVERSAL_TIMEOUT_MS,
            DataConnectionConnectTimeout = Program.UNIVERSAL_TIMEOUT_MS,
            DataConnectionReadTimeout = Program.UNIVERSAL_TIMEOUT_MS,
            ReadTimeout = Program.UNIVERSAL_TIMEOUT_MS,
        };

        client = new FtpClient(host, username, password, port, config);
    }

    private void EnsureConnected()
    {
        lock (client)
        {
            if (!client.IsStillConnected(1000)) client.Connect();
        }
    }

    public Task<bool> ExistsAsync(string path)
    {
        EnsureConnected();

        bool result;

        lock (client)
        {
            result = client.FileExists(path);
        }

        return Task.FromResult(result);
    }

    public Task DeleteAsync(string path)
    {
        EnsureConnected();

        lock (client)
        {
            client.DeleteFile(path);
        }

        return Task.CompletedTask;
    }

    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        EnsureConnected();

        MemoryStream ms;
        if (MemoryMarshal.TryGetArray(buffer, out var arraySegment) && arraySegment.Array != null)
        {
            ms = new MemoryStream(arraySegment.Array, arraySegment.Offset, arraySegment.Count, false);
        }
        else
        {
            ms = new MemoryStream();
            ms.Write(buffer.Span);
            ms.Seek(0, SeekOrigin.Begin);
        }

        using (ms)
        {
            lock (client)
            {
                if (overwrite)
                {
                    client.UploadStream(ms, path, FtpRemoteExists.Overwrite);
                }
                else
                {
                    if (client.FileExists(path))
                    {
                        throw new Exception($"{path} exists. Will not overwrite.");
                    }

                    client.UploadStream(ms, path);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        EnsureConnected();

        lock (client)
        {
            if (overwrite)
            {
                client.MoveFile(sourceFileName, destFileName, FtpRemoteExists.Overwrite);
            }
            else
            {
                client.MoveFile(sourceFileName, destFileName);
            }
        }

        return Task.CompletedTask;
    }

    public Task<Stream> GetStreamAsync(string path)
    {
        EnsureConnected();

        lock (client)
        {
            return !client.DownloadBytes(out var result, path)
                ? throw new InvalidOperationException("Download operation failed.")
                : Task.FromResult<Stream>(new MemoryStream(result));
        }
    }

    public Task<long> GetFileSizeAsync(string path)
    {
        EnsureConnected();

        long result;

        lock (client)
        {
            result = client.GetFileSize(path, 0);
        }

        return Task.FromResult(result);
    }
}