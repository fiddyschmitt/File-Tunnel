using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ft.Socks
{
    // Owns one SOCKS5 UDP-ASSOCIATE relay socket and demultiplexes inbound client datagrams by their
    // (per-datagram) destination, lazily opening one ft udp:// tunnel sub-connection per distinct
    // destination - exactly the shape UdpServer uses (one socket → N connections via a dictionary +
    // ConnectionAccepted), but keyed by destination and with the SOCKS UDP header parsed off each datagram.
    //
    // A single demux thread is the sole reader of the relay socket (so, unlike ft's shared-socket UdpStream
    // usage, there is no cross-destination datagram-stealing race). Lifetime is owned by SocksServer and
    // tied to the TCP control connection: SocksServer calls Dispose() when that connection closes.
    public class SocksUdpAssociation(UdpClient relaySocket, IPEndPoint? declaredClientEndpoint, Action<Stream, string> fireConnectionAccepted) : IDisposable, ISocksUdpSink
    {
        readonly ConcurrentDictionary<string, SocksUdpStream> streamsByDest = new();
        readonly object sendLock = new();
        volatile IPEndPoint? clientEndpoint;
        volatile bool disposed;

        public void Start()
        {
            Threads.StartNew(DemuxLoop, "SOCKS UDP demux");
        }

        void DemuxLoop()
        {
            while (!disposed)
            {
                byte[] data;
                var from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    data = relaySocket.Receive(ref from);
                }
                catch
                {
                    break;   // relay socket closed (Dispose) or errored → end the loop
                }

                if (!AcceptFromClient(from)) continue;                              // client-source filter
                if (!SocksNegotiator.TryParseUdpDatagram(data, out var parsed)) continue;   // malformed → drop
                if (parsed.Frag != 0) continue;                                     // fragmentation unsupported → drop

                var stream = streamsByDest.GetOrAdd(parsed.DestinationString, key =>
                {
                    var created = new SocksUdpStream(this, key, parsed.ReplyHeaderPrefix);

                    // Fire off the demux thread: EstablishConnection/EnqueueToSend can block up to the tunnel
                    // timeout when the SendQueue is full, and the demux loop must keep draining other flows.
                    Threads.StartNew(() => fireConnectionAccepted(created, key), $"SOCKS UDP sub {key}");
                    return created;
                });

                stream.AddToReadQueue(parsed.ExtractData());
            }
        }

        // Only this (single) demux thread mutates clientEndpoint, so the ??= is race-free. If the client
        // declared a concrete source in its ASSOCIATE request, require it; otherwise learn on first datagram
        // and pin to it thereafter (RFC 1928's expectation for a no-auth local relay).
        bool AcceptFromClient(IPEndPoint from)
        {
            if (declaredClientEndpoint != null && !from.Equals(declaredClientEndpoint)) return false;

            clientEndpoint ??= from;
            return from.Equals(clientEndpoint);
        }

        public void SendToClient(byte[] packet)
        {
            var ep = clientEndpoint;
            if (ep == null || disposed) return;

            lock (sendLock)
            {
                try { relaySocket.Send(packet, packet.Length, ep); } catch { }
            }
        }

        public void Remove(string destinationKey)
        {
            streamsByDest.TryRemove(destinationKey, out _);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Ordering matters: close the relay socket first to unblock/exit the demux loop, then close each
            // per-destination stream (→ relay1 EOF → RelayFinished → SharedFileStream.Close → TearDown →
            // far-side UDP socket closed). Snapshot before iterating: each Close() calls back into Remove().
            try { relaySocket.Close(); } catch { }

            foreach (var stream in streamsByDest.Values.ToArray())
            {
                try { stream.Close(); } catch { }
            }
            streamsByDest.Clear();

            GC.SuppressFinalize(this);
        }
    }
}
