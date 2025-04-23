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

    public class ConnectionAcceptedEventArgs(Stream stream, string destinationStr)
    {
        public Stream Stream { get; } = stream;
        public string DestinationEndpointString { get; } = destinationStr;
    }
}
