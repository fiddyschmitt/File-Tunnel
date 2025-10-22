using ft.Commands;
using ft.Listeners;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class SharedFileStream : Stream
    {
        public void EstablishConnection(string destinationEndpointStr)
        {
            SharedFileManager.Connect(ConnectionId, destinationEndpointStr);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public SharedFileManager SharedFileManager { get; }
        public int ConnectionId { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        byte[]? currentData = null;
        int currentDataIndex;

        public SharedFileStream(SharedFileManager sharedFileManager, int connectionId)
        {
            SharedFileManager = sharedFileManager;
            ConnectionId = connectionId;

            //File.Create(sentFile).Close();
            //File.Create(receivedFile).Close();
        }

        //string sentFile = $"diag-sent-{Environment.MachineName}.txt";
        //string receivedFile = $"diag-received-{Environment.MachineName}.txt";

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (currentData == null || currentData.Length == currentDataIndex)
            {
                currentData = SharedFileManager.Read(ConnectionId);
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

                //var received = new ReadOnlySpan<byte>(buffer, offset, toCopy);
                //File.AppendAllLines(receivedFile, [$"{received.Length:N0} bytes", Convert.ToBase64String(received.ToArray())]);

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
            // Always copy to decouple from caller’s buffer
            var toSend = new byte[count];
            Buffer.BlockCopy(buffer, offset, toSend, 0, count);

            var forwardCommand = new Forward(ConnectionId, toSend);

            while (true)
            {
                var enqueuedSuccessfully = SharedFileManager.EnqueueToSend(forwardCommand);

                if (enqueuedSuccessfully) break;
            }
        }

        public override void Close()
        {
            base.Close();

            SharedFileManager.TearDown(ConnectionId);
        }
    }
}
