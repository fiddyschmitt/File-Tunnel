using ft.Commands;
using ft.Listeners;
using ft.Socks;
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
                // The whole handler is wrapped: parsing the peer-supplied endpoint (Split/AsEndpoint) and the
                // UDP setup (Bind) can throw on a malformed/unresolvable value or an occupied --udp-send-from
                // port. This runs on a Threads.StartNew worker, so an escape would (before its safety net) kill
                // the process; here we log and tell the counterpart to tear the connection down instead.
                try
                {
                    var connectToTokens = connectionDetails.DestinationEndpointString.Split(["://"], StringSplitOptions.None);
                    if (connectToTokens.Length < 2)
                    {
                        throw new Exception($"Malformed destination endpoint '{connectionDetails.DestinationEndpointString}' (expected proto://host:port).");
                    }

                    var protocol = connectToTokens[0];
                    var destinationEndpointStr = connectToTokens[1];

                    var destinationEndpoint = destinationEndpointStr.AsEndpoint();

                    if (protocol.Equals("tcp"))
                    {
                        var connectStopwatch = Stopwatch.StartNew();

                        var tcpClient = new TcpClient();
                        SocketException? lastError = null;

                        //We try to connect in a loop, because sometimes the counterpart considers the tunnel to be online before this side does (leading it to try to setup the connection before this side is quite ready).
                        while (true)
                        {
                            try
                            {
                                tcpClient.Connect(destinationEndpoint);
                                break;
                            }
                            catch (SocketException se)
                            {
                                lastError = se;
                                if (connectStopwatch.ElapsedMilliseconds > tunnelTimeoutMilliseconds) break;
                                Delay.Wait(1000);
                            }
                            catch
                            {
                                if (connectStopwatch.ElapsedMilliseconds > tunnelTimeoutMilliseconds) break;
                                Delay.Wait(1000);
                            }
                        }

                        if (tcpClient.Connected)
                        {
                            Program.Log($"Connected to {destinationEndpointStr}");

                            //Report success so a SOCKS host can send an accurate reply. Sent before any
                            //relayed bytes; harmlessly discarded by non-SOCKS connections (no waiter).
                            SendConnectResultIfPossible(connectionDetails.Stream, ConnectStatus.Success);

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
                            Program.Log($"Could not connect to {destinationEndpointStr}: {lastError?.SocketErrorCode.ToString() ?? "timed out"}");

                            //Report the accurate failure reason to a waiting SOCKS host, then tear down.
                            SendConnectResultIfPossible(connectionDetails.Stream, MapSocketError(lastError));

                            if (connectionDetails.Stream is SharedFileStream sfs)
                            {
                                sfs.Close();
                            }
                        }
                    }
                    else if (protocol.Equals("udp"))
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
                }
                catch (Exception ex)
                {
                    Program.Log($"Error establishing connection to '{connectionDetails.DestinationEndpointString}': {ex.Message}");

                    //Make sure a waiting SOCKS host doesn't hang for the full timeout on an unexpected error.
                    SendConnectResultIfPossible(connectionDetails.Stream, ConnectStatus.GeneralFailure);

                    if (connectionDetails.Stream is SharedFileStream sharedFileStream)
                    {
                        Program.Log($"Instructing counterpart to tear down connection {sharedFileStream.ConnectionId}");
                        sharedFileStream.Close();
                    }
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
                 .Select(remoteForwardStr => NetworkUtilities.IsDynamicForwardSpec(remoteForwardStr)
                                                ? new CreateListener("socks", remoteForwardStr)   //bare [bind:]port → remote SOCKS proxy
                                                : new CreateListener("tcp", remoteForwardStr))
                 .ToList()
                 .ForEach(remoteForwardCommand => SharedFileManager.EnqueueToSend(remoteForwardCommand));

            RemoteUdpForwards
                 .Select(remoteForwardStr => new CreateListener("udp", remoteForwardStr))
                 .ToList()
                 .ForEach(remoteForwardCommand => SharedFileManager.EnqueueToSend(remoteForwardCommand));
        }

        //Reports a dial outcome back to a SOCKS host (a no-op for non-SharedFileStream / non-SOCKS peers).
        void SendConnectResultIfPossible(System.IO.Stream stream, ConnectStatus status)
        {
            if (stream is SharedFileStream sfs)
            {
                SharedFileManager.SendConnectResult(sfs.ConnectionId, (byte)status);
            }
        }

        static ConnectStatus MapSocketError(SocketException? socketException) => socketException?.SocketErrorCode switch
        {
            null => ConnectStatus.TtlExpired,   //no socket error captured → we timed out retrying
            SocketError.ConnectionRefused => ConnectStatus.ConnectionRefused,
            SocketError.HostUnreachable => ConnectStatus.HostUnreachable,
            SocketError.NetworkUnreachable => ConnectStatus.HostUnreachable,
            SocketError.HostNotFound => ConnectStatus.HostUnreachable,
            SocketError.TimedOut => ConnectStatus.TtlExpired,
            _ => ConnectStatus.GeneralFailure
        };
    }
}