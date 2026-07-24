using ft.Http;
using System;
using System.Net.Sockets;
using System.Threading;

namespace ft.Listeners
{
    // HTTP CONNECT proxy listener - the HTTP analogue of SocksServer. Per connection it reads an HTTP
    // CONNECT handshake to learn the destination, then fires ConnectionAccepted with "tcp://host:port" plus
    // a reply callback the tunnel invokes once the far side reports the dial result (200 / 502 / 504).
    public class HttpProxyServer : StreamEstablisher
    {
        TcpListener? listener;
        Thread? listenerTask;
        bool stopRequested = false;

        public HttpProxyServer(string listenOnEndpointStr)
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
            Program.Log($"Started HTTP proxy on {ListenOnEndpointStr}");

            listenerTask = Threads.StartNew(() =>
            {
                try
                {
                    while (true)
                    {
                        var client = listener.AcceptTcpClient();

                        // The handshake reads from the client, so run it on a per-connection worker rather
                        // than inline - a slow or silent client must not stall the accept loop for everyone.
                        Threads.StartNew(() => Negotiate(client), $"HTTP CONNECT {client.Client.RemoteEndPoint}");
                    }
                }
                catch (Exception ex)
                {
                    if (!stopRequested)
                    {
                        Program.Log($"HttpProxyServer error ({ListenOnEndpointStr}): {ex.Message}");
                    }
                }
            }, $"HTTP proxy listener {ListenOnEndpointStr}");
        }

        void Negotiate(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();

                var request = HttpProxyNegotiator.Read(stream);

                // Written by this callback AFTER the far side reports its dial result (see LocalToRemoteTunnel),
                // so the 200/502 is accurate and is guaranteed to reach the client before any relayed bytes.
                void WriteReply(byte status) => HttpProxyNegotiator.WriteReply(stream, status);

                ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(stream, request.Destination, WriteReply));
            }
            catch (Exception ex)
            {
                Program.Log($"HTTP CONNECT handshake failed: {ex.Message}");
                try { client.Close(); } catch { }
            }
        }

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(HttpProxyServer)} ({ListenOnEndpointStr}): Stopping. Reason: {reason}");

            stopRequested = true;

            try { listener?.Stop(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            try { listenerTask?.Join(); }
            catch (Exception ex) { Program.Log($"Stop(): {ex}"); }

            stopRequested = false;
        }
    }
}
