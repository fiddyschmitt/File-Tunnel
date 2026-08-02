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

        //Deliberately NO ranged-read override here: FTP inherits IFileAccess.ReadBytes's whole-file
        //fallback. FluentFTP can do it (DownloadBytes takes restart/stop positions), but truncating the
        //data connection desynchronises vsftpd - measured as "500 OOPS: priv_sock_get_cmd" on every
        //subsequent operation, because this backend shares ONE FtpClient across all of them, so the
        //session never recovers and the tunnel never comes online. All 3 FTP end-to-end rows timed out
        //at 152s with the override; all 3 pass in ~10-24s without it.

        public void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes, bool overwrite = true)
        {
            Reconnect();

            //FTP is the one backend that still needs an exact-size array: FluentFTP's UploadBytes takes a
            //byte[] with no offset/length, and the caller hands us a slice of a larger buffer whose spare
            //capacity must not be uploaded. Copied outside the lock so it doesn't hold up the single
            //shared connection. Every other backend writes the caller's buffer without copying.
            var buffer = bytes.ToArray();

            lock (client)
            {
                if (overwrite)
                {
                    client.UploadBytes(buffer, path, FtpRemoteExists.Overwrite);
                }
                else
                {
                    if (client.FileExists(path))
                    {
                        throw new Exception($"{path} exists. Will not overwrite.");
                    }

                    client.UploadBytes(buffer, path);
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
