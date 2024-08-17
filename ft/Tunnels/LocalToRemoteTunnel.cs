using ft.Listeners;
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
        int connectionCount = 0;
        public MultiServer LocalListeners { get; }

        public LocalToRemoteTunnel(MultiServer localListeners, SharedFileManager sharedFileManager, long writeFileSize, int readDurationMillis)
        {
            LocalListeners = localListeners;

            sharedFileManager.OnlineStatusChanged += (sender, args) =>
            {
                if (args.IsOnline)
                {
                    localListeners.Start();
                }
                else
                {
                    localListeners.Stop();
                    localListeners.RemoveListenersOriginatingFromRemote();
                    sharedFileManager.TearDownAllConnections();
                }
            };

            sharedFileManager.SessionChanged += (sender, args) =>
            {
                localListeners.RemoveListenersOriginatingFromRemote();
            };

            localListeners.ConnectionAccepted += (sender, connectionDetails) =>
            {
                var connectionId = Interlocked.Increment(ref connectionCount);
                var secondaryStream = new SharedFileStream(sharedFileManager, connectionId);
                secondaryStream.EstablishConnection(connectionDetails.DestinationEndpointString);

                var relay1 = new Relay(connectionDetails.Stream, secondaryStream, writeFileSize, readDurationMillis);
                var relay2 = new Relay(secondaryStream, connectionDetails.Stream, writeFileSize, readDurationMillis);

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
