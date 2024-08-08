using ft.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ft.Listeners
{
    public class MultiServer : StreamEstablisher
    {
        readonly List<StreamEstablisher> servers = [];

        public MultiServer()
        {

        }

        public void Add(string protocol, List<string> forwardsStrings)
        {
            var servers = forwardsStrings
                            .Select(forwardStr =>
                            {
                                (var listenEndpoint, var destinationEndpoint) = NetworkUtilities.ParseForwardString(forwardStr);

                                var fullDestinationEndpoint = $"{protocol}://{destinationEndpoint}";

                                StreamEstablisher? listener = null;
                                if (protocol == "tcp")
                                {
                                    listener = new TcpServer(listenEndpoint, fullDestinationEndpoint);
                                }

                                if (protocol == "udp")
                                {
                                    listener = new UdpServer(listenEndpoint, fullDestinationEndpoint);
                                }

                                if (listener == null)
                                {
                                    throw new Exception($"Could not instantiate listener for: {forwardStr}");
                                }

                                Program.Log($"Initialised {protocol} forwarder for: {listenEndpoint} -> {destinationEndpoint}");

                                listener.StreamEstablished += (sender, args) =>
                                {
                                    StreamEstablished?.Invoke(this, args);
                                };

                                return listener;
                            })
                            .ToList();

            this.servers.AddRange(servers);
        }

        public override void Start()
        {
            servers
                .ForEach(server => server.Start());
        }

        public override void Stop()
        {
            servers
                .ForEach(server => server.Stop());
        }
    }
}
