using ft.Socks;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ft.Listeners
{
    // Dynamic (SOCKS) forwarding listener - the ft equivalent of SSH's -D / -R dynamic forwarding.
    // Like TcpServer, but instead of a fixed destination it performs a per-connection SOCKS4/4a/5
    // handshake to learn the destination, then fires ConnectionAccepted with that dynamic
    // "tcp://host:port" plus a reply callback the tunnel invokes once the far side reports the dial
    // result (so the client gets an accurate SOCKS reply).
    public class SocksServer : StreamEstablisher
    {
        TcpListener? listener;
        Thread? listenerTask;
        bool stopRequested = false;

        //Live SOCKS5 UDP associations (each owns a relay socket + its udp:// sub-connections). Tracked so
        //Stop() can reclaim them; the primary teardown is the control-connection close in HandleUdpAssociate.
        readonly ConcurrentDictionary<SocksUdpAssociation, byte> associations = new();

        public SocksServer(string listenOnEndpointStr)
        {
            ListenOnEndpointStr = listenOnEndpointStr;

            if (!listenOnEndpointStr.IsValidEndpoint())
            {
                Program.Log($"Invalid endpoint specified: {listenOnEndpointStr}");
                Program.Log($"Please specify IP:Port or [IPV6]:Port");
                Environment.Exit(1);
            }
        }

        public string ListenOnEndpointStr { get; }

        public override void Start()
        {
            var listenEndpoint = ListenOnEndpointStr.AsEndpoint();

            listener = new TcpListener(listenEndpoint);
            listener.Start();
            Program.Log($"Started SOCKS proxy on {ListenOnEndpointStr}");

            listenerTask = Threads.StartNew(() =>
            {
                try
                {
                    while (true)
                    {
                        var client = listener.AcceptTcpClient();

                        // The handshake reads from the client, so run it on a per-connection worker rather
                        // than inline - a slow or silent client must not stall the accept loop for everyone.
                        Threads.StartNew(() => Negotiate(client), $"SOCKS handshake {client.Client.RemoteEndPoint}");
                    }
                }
                catch (Exception ex)
                {
                    if (!stopRequested)
                    {
                        Program.Log($"SocksServer error ({ListenOnEndpointStr}): {ex.Message}");
                    }
                }
            }, $"SOCKS listener {ListenOnEndpointStr}");
        }

        void Negotiate(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();

                var request = SocksNegotiator.Read(stream);

                if (request.Command == SocksCommand.UdpAssociate)
                {
                    HandleUdpAssociate(client, stream, request);
                    return;
                }

                // The reply is written by this callback AFTER the far side reports its dial result (see
                // LocalToRemoteTunnel), so it carries an accurate SOCKS code and is guaranteed to reach the
                // client before any relayed bytes.
                void WriteReply(byte status) => SocksNegotiator.WriteReply(stream, request.Version, status);

                ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(stream, request.Destination, WriteReply));
            }
            catch (Exception ex)
            {
                Program.Log($"SOCKS handshake failed: {ex.Message}");
                try { client.Close(); } catch { }
            }
        }

        // SOCKS5 UDP ASSOCIATE: bind a relay UDP socket, tell the client where to send its datagrams, and
        // keep this control connection open for the association's lifetime (RFC 1928). The association
        // demuxes datagrams by destination and rides the normal udp:// tunnel path, one sub-connection each.
        void HandleUdpAssociate(TcpClient client, Stream controlStream, SocksRequest request)
        {
            // Bind the relay to the concrete address the client reached us on (an accepted socket's
            // LocalEndPoint is never the wildcard), so the BND.ADDR we advertise is actually reachable.
            var controlLocalAddr = ((IPEndPoint)client.Client.LocalEndPoint!).Address;

            UdpClient relay;
            try
            {
                relay = new UdpClient(new IPEndPoint(controlLocalAddr, 0));
            }
            catch (Exception ex)
            {
                Program.Log($"SOCKS UDP associate: could not bind relay socket: {ex.Message}");
                try { SocksNegotiator.WriteSocks5UdpReply(controlStream, 0x01, new IPEndPoint(controlLocalAddr, 0)); } catch { }
                try { client.Close(); } catch { }
                return;
            }

            var relayPort = ((IPEndPoint)relay.Client.LocalEndPoint!).Port;
            SocksNegotiator.WriteSocks5UdpReply(controlStream, 0x00, new IPEndPoint(controlLocalAddr, relayPort));

            var association = new SocksUdpAssociation(
                relay,
                request.UdpClientDeclaredEndpoint,
                (subStream, dest) => ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(subStream, dest)));   // no onConnectResult → udp subs relay immediately

            associations.TryAdd(association, 0);
            association.Start();

            Program.Log($"Started SOCKS UDP relay on {controlLocalAddr}:{relayPort}");

            try
            {
                // The association lives as long as the control TCP connection. Drain (ignore) anything the
                // client sends and block until it closes the connection (EOF) or it errors.
                var buffer = new byte[512];
                while (controlStream.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch { }
            finally
            {
                associations.TryRemove(association, out _);
                association.Dispose();
                try { client.Close(); } catch { }
            }
        }

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(SocksServer)} ({ListenOnEndpointStr}): Stopping. Reason: {reason}");

            stopRequested = true;

            try { listener?.Stop(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            try { listenerTask?.Join(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            foreach (var association in associations.Keys.ToArray())
            {
                try { association.Dispose(); } catch { }
                associations.TryRemove(association, out _);
            }

            stopRequested = false;
        }
    }
}
