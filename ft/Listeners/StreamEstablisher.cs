using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Listeners
{
    public abstract class StreamEstablisher
    {
        public EventHandler<ConnectionAcceptedEventArgs>? ConnectionAccepted;
        public abstract void Start();
        public abstract void Stop(string reason);
    }

    public class ConnectionAcceptedEventArgs(Stream stream, string destinationStr, Action<byte>? onConnectResult = null)
    {
        public Stream Stream { get; } = stream;
        public string DestinationEndpointString { get; } = destinationStr;

        //Set only for dynamic (SOCKS) connections: invoked with the ConnectStatus once the far side reports
        //the dial result, so the SOCKS host can write an accurate reply before relaying begins.
        public Action<byte>? OnConnectResult { get; } = onConnectResult;
    }
}
