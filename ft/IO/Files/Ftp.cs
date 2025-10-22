using FluentFTP;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.IO.Files
{
    public class Ftp : IFileAccess
    {
        readonly FtpClient client;

        public Ftp(string host, int port, string username, string password)
        {
            var config = new FtpConfig()
            {
                ConnectTimeout = Program.UNIVERSAL_TIMEOUT_MS,
                DataConnectionConnectTimeout = Program.UNIVERSAL_TIMEOUT_MS,
                DataConnectionReadTimeout = Program.UNIVERSAL_TIMEOUT_MS,
                ReadTimeout = Program.UNIVERSAL_TIMEOUT_MS,
            };

            client = new FtpClient(host, username, password, port, config);
        }

        void Reconnect()
        {
            lock (client)
            {
                if (!client.IsStillConnected(1000)) client.Connect();
            }
        }

        public void Delete(string path)
        {
            Reconnect();

            lock (client)
            {
                client.DeleteFile(path);
            }
        }

        public bool Exists(string path)
        {
            Reconnect();

            var result = false;

            lock (client)
            {
                result = client.FileExists(path);
            }

            return result;
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            Reconnect();

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
        }

        public byte[] ReadAllBytes(string path)
        {
            Reconnect();

            lock (client)
            {
                client.DownloadBytes(out var result, path);

                return result;
            }
        }

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            Reconnect();

            lock (client)
            {
                if (overwrite)
                {
                    client.UploadBytes(bytes, path, FtpRemoteExists.Overwrite);
                }
                else
                {
                    if (client.FileExists(path))
                    {
                        throw new Exception($"{path} exists. Will not overwrite.");
                    }

                    client.UploadBytes(bytes, path);
                }
            }
        }

        public long GetFileSize(string path)
        {
            Reconnect();

            var result = 0L;

            lock (client)
            {
                result = client.GetFileSize(path, 0);
            }

            return result;
        }
    }
}
