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
        public MultiServer LocalListeners { get; }
        public SharedFileManager SharedFileManager { get; }

        public LocalToRemoteTunnel(MultiServer localListeners, SharedFileManager sharedFileManager, int purgeSizeInBytes, int readDurationMillis)
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
                var connectionId = SharedFileManager.GenerateUniqueConnectionId();
                var secondaryStream = new SharedFileStream(sharedFileManager, connectionId);
                secondaryStream.EstablishConnection(connectionDetails.DestinationEndpointString);

                var relay1 = new Relay(connectionDetails.Stream, secondaryStream, purgeSizeInBytes, readDurationMillis);
                var relay2 = new Relay(secondaryStream, connectionDetails.Stream, purgeSizeInBytes, readDurationMillis);

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
