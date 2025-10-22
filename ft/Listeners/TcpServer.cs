using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace ft.Listeners
{
    public class TcpServer : StreamEstablisher
    {
        TcpListener? listener;
        Thread? listenerTask;

        public TcpServer(string listenOnEndpointStr, string forwardToEndpointStr)
        {
            ListenOnEndpointStr = listenOnEndpointStr;
            ForwardToEndpointStr = forwardToEndpointStr;

            if (!listenOnEndpointStr.IsValidEndpoint())
            {
                Program.Log($"Invalid endpoint specified: {listenOnEndpointStr}");
                Program.Log($"Please specify IP:Port or Hostname:Port or [IPV6]:Port");
                Environment.Exit(1);
            }
        }

        public string ListenOnEndpointStr { get; }
        public string ForwardToEndpointStr { get; }

        public override void Start()
        {
            var listenEndpoint = ListenOnEndpointStr.AsEndpoint();

            //start listener here so that it's ready should the very next message be a Connect
            listener = new TcpListener(listenEndpoint);
            listener.Start();
            Program.Log($"Started listening on TCP {ListenOnEndpointStr}");

            listenerTask = Threads.StartNew(() =>
            {
                try
                {
                    while (true)
                    {
                        var client = listener.AcceptTcpClient();

                        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                        Program.Log($"Accepted connection from {client.Client.RemoteEndPoint}");

                        var clientStream = client.GetStream();

                        ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(clientStream, ForwardToEndpointStr));
                    }
                }
                catch (Exception ex)
                {
                    if (!stopRequested)
                    {
                        Program.Log($"TcpServer error ({ListenOnEndpointStr}): {ex.Message}");
                    }
                }

            }, $"TCP listener {ListenOnEndpointStr}");
        }

        bool stopRequested = false;

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(TcpServer)} ({ListenOnEndpointStr}): Stopping. Reason: {reason}");

            stopRequested = true;

            try
            {
                listener?.Stop();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }

            try
            {
                listenerTask?.Join();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }

            stopRequested = false;
        }
    }
}
