using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace ft.Listeners
{
    public class TcpServer : StreamEstablisher
    {
        TcpListener? listener;
        readonly Task listenerTask;

        public TcpServer(string endpointStr)
        {
            var listenEndpoint = IPEndPoint.Parse(endpointStr);

            listenerTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    listener = new TcpListener(listenEndpoint);
                    listener.Start();
                    Program.Log($"Listening on {endpointStr}");

                    while (true)
                    {
                        var client = listener.AcceptTcpClient();

                        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                        Program.Log($"Accepted connection from {client.Client.RemoteEndPoint}");

                        var clientStream = client.GetStream();

                        StreamEstablished?.Invoke(this, clientStream);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"TcpServer error: {ex}");
                }
            }, TaskCreationOptions.LongRunning);
        }

        public override void Stop()
        {
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
                listenerTask.Wait();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }

        }
    }
}
