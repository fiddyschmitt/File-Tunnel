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
                ConnectTimeout = 4000,
                DataConnectionConnectTimeout = 4000,
                DataConnectionReadTimeout = 4000,
                ReadTimeout = 4000,
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

        public void WriteAllBytes(string path, byte[] bytes)
        {
            Reconnect();

            lock (client)
            {
                client.UploadBytes(bytes, path, FtpRemoteExists.Overwrite);
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
