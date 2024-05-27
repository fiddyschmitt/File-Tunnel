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
        Task? listenerTask;
        string ListenEndpointStr { get; }

        public UdpServer(string listenEndpointStr)
        {
            ListenEndpointStr = listenEndpointStr;
        }

        public override void Start()
        {
            var listenEndpoint = IPEndPoint.Parse(ListenEndpointStr);

            listener = new UdpClient(listenEndpoint);

            var connections = new Dictionary<IPEndPoint, UdpStream>();

            listenerTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    while (true)
                    {
                        var remoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                        var data = listener.Receive(ref remoteIpEndPoint);

                        if (!connections.TryGetValue(remoteIpEndPoint, out UdpStream? udpStream))
                        {
                            udpStream = new UdpStream(listener, remoteIpEndPoint);
                            connections.Add(remoteIpEndPoint, udpStream);

                            StreamEstablished?.Invoke(this, udpStream);
                        }

                        udpStream.AddToReadQueue(data);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"UdpServer error: {ex}");
                }
            }, TaskCreationOptions.LongRunning);
        }

        public override void Stop()
        {
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
                listenerTask?.Wait();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }
        }
    }
}
