using ft.Listeners;
using ft.Socks;
using ft.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ft.Tunnels
{
    public class LocalToRemoteTunnel
    {
        public MultiServer LocalListeners { get; }
        public SharedFileManager SharedFileManager { get; }

        public LocalToRemoteTunnel(MultiServer localListeners, SharedFileManager sharedFileManager, long maxFileSizeBytes, int readDurationMillis)
        {
            LocalListeners = localListeners;
            SharedFileManager = sharedFileManager;

            sharedFileManager.OnlineStatusChanged += (sender, args) =>
            {
                if (args.IsOnline)
                {
                    localListeners.Start();
                }
                else
                {
                    var reason = "File tunnel is offline.";
                    localListeners.Stop(reason);
                    localListeners.RemoveListenersOriginatingFromRemote(reason);
                    sharedFileManager.TearDownAllConnections();
                }
            };

            sharedFileManager.SessionChanged += (sender, args) =>
            {
                localListeners.RemoveListenersOriginatingFromRemote("File tunnel session changed.");
            };

            localListeners.ConnectionAccepted += (sender, connectionDetails) =>
            {
                var connectionId = SharedFileManager.GenerateUniqueConnectionId();

                var secondaryStream = new SharedFileStream(sharedFileManager, connectionId);

                //Dynamic (SOCKS) connections carry a reply callback. Register a waiter, send the Connect,
                //then block for the dialing side's ConnectResult so we can return an ACCURATE SOCKS reply -
                //and, crucially, do so BEFORE any relaying starts, so the reply is the first thing the client
                //sees (relay2 must not push far-side bytes ahead of it). A normal -L/-U connection has no
                //callback and relays immediately, exactly as before.
                var isDynamic = connectionDetails.OnConnectResult != null;

                if (isDynamic)
                {
                    sharedFileManager.RegisterConnectResultWaiter(connectionId);
                }

                secondaryStream.EstablishConnection(connectionDetails.DestinationEndpointString);

                if (isDynamic)
                {
                    //Wait longer than the dialer's own connect-timeout so a slow-but-successful dial isn't
                    //misreported as a failure.
                    var status = sharedFileManager.AwaitConnectResult(connectionId, sharedFileManager.TunnelTimeoutMilliseconds * 2);

                    try { connectionDetails.OnConnectResult!(status); } catch { }

                    if (status != (byte)ConnectStatus.Success)
                    {
                        try { connectionDetails.Stream.Close(); } catch { }
                        secondaryStream.Close();
                        return;
                    }
                }

                var relay1 = new Relay(connectionDetails.Stream, secondaryStream, maxFileSizeBytes, readDurationMillis);
                var relay2 = new Relay(secondaryStream, connectionDetails.Stream, maxFileSizeBytes, readDurationMillis);

                void TearDown()
                {
                    relay1.Stop();
                    relay2.Stop();
                }

                relay1.RelayFinished += (s, a) => TearDown();
                relay2.RelayFinished += (s, a) => TearDown();
            };
        }
    }
}
