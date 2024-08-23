using ft.Listeners;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class UdpStream : Stream
    {
        public UdpStream(UdpClient client, IPEndPoint sendTo)
        {
            Client = client;
            SendTo = sendTo;

            Threads.StartNew(() =>
            {
                try
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        var remoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                        var data = client.Receive(ref remoteIpEndPoint);

                        AddToReadQueue(data);
                    }
                }
                catch
                {
                    //Program.Log($"UDP Read Pump: {ex}");
                }
            }, $"{nameof(UdpStream)} Receive loop");
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public UdpClient Client { get; }
        public IPEndPoint SendTo { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        byte[]? currentData = null;
        int currentDataIndex;

        readonly BlockingCollection<byte[]> toRead = [];
        public void AddToReadQueue(byte[] data)
        {
            toRead.Add(data);
        }


        readonly CancellationTokenSource cancellationTokenSource = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (currentData == null || currentData.Length == currentDataIndex)
            {
                currentData = toRead.Take(cancellationTokenSource.Token);
                currentDataIndex = 0;
            }

            if (currentData == null)
            {
                return 0;
            }
            else
            {
                var toCopy = count;
                toCopy = Math.Min(toCopy, currentData.Length - currentDataIndex);

                Array.Copy(currentData, currentDataIndex, buffer, offset, toCopy);
                currentDataIndex += toCopy;

                return toCopy;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Client.Send(buffer, count, SendTo);
        }
    }
}
