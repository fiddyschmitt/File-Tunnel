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
        readonly List<(StreamEstablisher Listener, bool OriginatedFromRemote, string FullLocalEndpoint)> servers = [];
        bool started = false;

        public MultiServer()
        {

        }

        public void Add(string protocol, string forwardStr, bool originatedFromRemote)
        {
            (var listenEndpoint, var destinationEndpoint) = NetworkUtilities.ParseForwardString(forwardStr);

            var fullLocalEndpoint = $"{protocol}://{listenEndpoint}";
            var fullDestinationEndpoint = $"{protocol}://{destinationEndpoint}";

            var alreadyExists = servers
                                    .Exists(server => server.FullLocalEndpoint == fullLocalEndpoint);
            if (alreadyExists) return;

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

            listener.ConnectionAccepted += (sender, args) =>
            {
                ConnectionAccepted?.Invoke(this, args);
            };

            servers.Add((listener, originatedFromRemote, fullLocalEndpoint));

            if (started)
            {
                listener.Start();
            }
        }

        public void Add(string protocol, IEnumerable<string> forwardsStrings, bool originatedFromRemote)
        {
            forwardsStrings
                .ToList()
                .ForEach(forwardsStr => Add(protocol, forwardsStr, originatedFromRemote));
        }

        public override void Start()
        {
            started = true;

            servers
                .ForEach(server => server.Listener.Start());
        }

        public override void Stop()
        {
            started = false;

            servers
                .ForEach(server => server.Listener.Stop());
        }

        public void RemoveListenersOriginatingFromRemote()
        {
            servers
                .RemoveAll(server =>
                {
                    try
                    {
                        if (server.OriginatedFromRemote)
                        {
                            server.Listener.Stop();
                        }
                    }
                    catch { }

                    return server.OriginatedFromRemote;
                });
        }
    }
}
