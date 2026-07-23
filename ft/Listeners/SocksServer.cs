using ft.Socks;
using System;
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

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(SocksServer)} ({ListenOnEndpointStr}): Stopping. Reason: {reason}");

            stopRequested = true;

            try { listener?.Stop(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            try { listenerTask?.Join(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            stopRequested = false;
        }
    }
}
