using ft.Commands;
using ft.Listeners;
using ft.Streams;
using ft.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ft.Tunnels
{
    public class RemoteToLocalTunnel
    {
        private readonly int tunnelTimeoutMilliseconds;

        public RemoteToLocalTunnel(List<string> remoteTcpForwards, List<string> remoteUdpForwards, SharedFileManager sharedFileManager, LocalToRemoteTunnel localToRemoteTunnel, string udpSendFrom, long maxFileSizeBytes, int readDurationMillis, int tunnelTimeoutMilliseconds)
        {
            RemoteTcpForwards = remoteTcpForwards;
            RemoteUdpForwards = remoteUdpForwards;
            SharedFileManager = sharedFileManager;
            this.tunnelTimeoutMilliseconds = tunnelTimeoutMilliseconds;

            sharedFileManager.OnlineStatusChanged += (sender, args) =>
            {
                if (args.IsOnline)
                {
                    ProvideRemoteForwardsToCounterpart();
                }
                else
                {
                    sharedFileManager.TearDownAllConnections();
                }
            };

            sharedFileManager.SessionChanged += (sender, args) =>
            {
                ProvideRemoteForwardsToCounterpart();
            };

            sharedFileManager.ConnectionAccepted += (sender, connectionDetails) =>
            {
                var connectToTokens = connectionDetails.DestinationEndpointString.Split(["://"], StringSplitOptions.None);
                var protocol = connectToTokens[0];
                var destinationEndpointStr = connectToTokens[1];

                var destinationEndpoint = destinationEndpointStr.AsEndpoint();

                if (protocol.Equals("tcp"))
                {
                    try
                    {
                        var connectStopwatch = Stopwatch.StartNew();

                        var tcpClient = new TcpClient();

                        //We try to connect in a loop, because sometimes the counterpart considers the tunnel to be online before this side does (leading it to try to setup the connection before this side is quite ready).
                        while (true)
                        {
                            try
                            {
                                tcpClient.Connect(destinationEndpoint);
                                break;
                            }
                            catch
                            {
                                if (connectStopwatch.ElapsedMilliseconds > tunnelTimeoutMilliseconds)
                                {
                                    throw new Exception($"Timed out while connecting to {destinationEndpointStr}");
                                }

                                Delay.Wait(1000);
                            }
                        }

                        if (tcpClient.Connected)
                        {
                            Program.Log($"Connected to {destinationEndpointStr}");

                            var relay1 = new Relay(tcpClient.GetStream(), connectionDetails.Stream, maxFileSizeBytes, readDurationMillis);
                            var relay2 = new Relay(connectionDetails.Stream, tcpClient.GetStream(), maxFileSizeBytes, readDurationMillis);

                            void TearDown()
                            {
                                relay1.Stop();
                                relay2.Stop();
                            }

                            relay1.RelayFinished += (s, a) => TearDown();
                            relay2.RelayFinished += (s, a) => TearDown();
                        }
                        else
                        {
                            Program.Log($"Could not connect to: {destinationEndpointStr}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"Error during connection to {destinationEndpointStr}. {ex.Message}");

                        if (connectionDetails.Stream is SharedFileStream sharedFileStream)
                        {
                            Program.Log($"Instructing counterpart to tear down connection {sharedFileStream.ConnectionId}");
                            sharedFileStream.Close();
                        }
                    }
                }

                if (protocol.Equals("udp"))
                {
                    var sendFromEndpoint = udpSendFrom.AsEndpoint();

                    var udpClient = new UdpClient()
                    {
                        EnableBroadcast = true
                    };

                    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpClient.Client.Bind(sendFromEndpoint);

                    var udpStream = new UdpStream(udpClient, destinationEndpoint);

                    var relay1 = new Relay(udpStream, connectionDetails.Stream, maxFileSizeBytes, readDurationMillis);
                    var relay2 = new Relay(connectionDetails.Stream, udpStream, maxFileSizeBytes, readDurationMillis);

                    void TearDown()
                    {
                        relay1.Stop();
                        relay2.Stop();
                    }

                    relay1.RelayFinished += (s, a) => TearDown();
                    relay2.RelayFinished += (s, a) => TearDown();
                }
            };

            sharedFileManager.CreateLocalListenerRequested += (sender, createLocalListenerArgs) =>
            {
                localToRemoteTunnel.LocalListeners.Add(createLocalListenerArgs.Protocol, createLocalListenerArgs.ForwardString, true);
            };
        }

        public List<string> RemoteTcpForwards { get; }
        public List<string> RemoteUdpForwards { get; }
        public SharedFileManager SharedFileManager { get; }

        void ProvideRemoteForwardsToCounterpart()
        {
            //Tell the remote side to start the Remote Forwards

            RemoteTcpForwards
                 .Select(remoteForwardStr => new CreateListener("tcp", remoteForwardStr))
                 .ToList()
                 .ForEach(remoteForwardCommand => SharedFileManager.EnqueueToSend(remoteForwardCommand));

            RemoteUdpForwards
                 .Select(remoteForwardStr => new CreateListener("udp", remoteForwardStr))
                 .ToList()
                 .ForEach(remoteForwardCommand => SharedFileManager.EnqueueToSend(remoteForwardCommand));
        }
    }
}