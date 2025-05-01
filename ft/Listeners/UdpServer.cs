using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ft.Streams;
using System.Threading;

namespace ft.Listeners
{
    public class UdpServer : StreamEstablisher
    {
        UdpClient? listener;
        Thread? listenerTask;

        public UdpServer(string listenOnEndpointStr, string forwardToEndpointStr)
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

            listener = new UdpClient()
            {
                EnableBroadcast = true
            };

            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(listenEndpoint);

            var connections = new Dictionary<IPEndPoint, UdpStream>();

            listenerTask = Threads.StartNew(() =>
            {
                try
                {
                    Program.Log($"Started listening on UDP {ListenOnEndpointStr}");

                    while (true)
                    {
                        var remoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                        var data = listener.Receive(ref remoteIpEndPoint);

                        if (!connections.TryGetValue(remoteIpEndPoint, out var udpStream))
                        {
                            udpStream = new UdpStream(listener, remoteIpEndPoint);
                            connections.Add(remoteIpEndPoint, udpStream);

                            ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(udpStream, ForwardToEndpointStr));
                        }

                        udpStream.AddToReadQueue(data);
                    }
                }
                catch (Exception ex)
                {
                    if (!stopRequested)
                    {
                        Program.Log($"UdpServer error ({ListenOnEndpointStr}): {ex.Message}");
                    }
                }

            }, $"UDP listener {listenEndpoint}");
        }

        bool stopRequested = false;

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(UdpServer)} ({ListenOnEndpointStr}): Stopping. Reason: {reason}");

            stopRequested = true;

            try
            {
                listener?.Close();
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
