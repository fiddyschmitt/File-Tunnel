using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ft.Socks
{
    // The SocksUdpStream's view of its owning association: where reply datagrams go, and how to de-register
    // on close. Kept as an interface so the stream can be unit-tested without a real relay socket.
    public interface ISocksUdpSink
    {
        void SendToClient(byte[] packet);
        void Remove(string destinationKey);
    }

    // A passive per-destination Stream for one SOCKS5 UDP association. Unlike ft's UdpStream it does NOT
    // start its own receive loop: many of these share the association's single relay socket, so a
    // self-receiving loop per stream would steal datagrams across destinations with no source filtering.
    // The association's single demux loop feeds this stream via AddToReadQueue; Write prepends the SOCKS
    // reply header and hands the datagram to the association to send to the client on the shared socket.
    //
    // Fired to the tunnel as the "client side" of a udp://dest sub-connection: relay1 reads client
    // datagrams from here (→ tunnel → far side sendto), relay2 writes destination replies here (→ client).
    public class SocksUdpStream : Stream
    {
        readonly ISocksUdpSink owner;
        readonly string destinationKey;
        readonly byte[] replyHeaderPrefix;

        readonly BlockingCollection<byte[]> toRead = [];
        readonly CancellationTokenSource cts = new();
        volatile bool closed;

        byte[]? currentData;
        int currentDataIndex;

        public SocksUdpStream(ISocksUdpSink owner, string destinationKey, byte[] replyHeaderPrefix)
        {
            this.owner = owner;
            this.destinationKey = destinationKey;
            this.replyHeaderPrefix = replyHeaderPrefix;
        }

        // Called only by the association's demux loop (single producer).
        public void AddToReadQueue(byte[] data)
        {
            if (!closed) toRead.Add(data);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (currentData == null || currentDataIndex == currentData.Length)
            {
                try
                {
                    currentData = toRead.Take(cts.Token);
                }
                catch (OperationCanceledException) { return 0; }   // Close() → clean EOF so the relay finishes
                catch (InvalidOperationException) { return 0; }     // CompleteAdding + drained
                currentDataIndex = 0;
            }

            var toCopy = Math.Min(count, currentData.Length - currentDataIndex);
            Array.Copy(currentData, currentDataIndex, buffer, offset, toCopy);
            currentDataIndex += toCopy;
            return toCopy;   // one datagram's worth per drain - preserves the datagram boundary
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (closed) return;

            // One Write == one reply datagram to the client: SOCKS reply header + payload.
            var packet = new byte[replyHeaderPrefix.Length + count];
            Buffer.BlockCopy(replyHeaderPrefix, 0, packet, 0, replyHeaderPrefix.Length);
            Buffer.BlockCopy(buffer, offset, packet, replyHeaderPrefix.Length, count);

            owner.SendToClient(packet);
        }

        // Idempotent (called by relay1.Stop, relay2.Stop, and association Dispose). Must NOT touch the
        // shared relay socket - only unblock our own Read and de-register from the association.
        public override void Close()
        {
            if (closed) return;
            closed = true;

            try { cts.Cancel(); } catch { }
            try { toRead.CompleteAdding(); } catch { }
            owner.Remove(destinationKey);

            base.Close();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
