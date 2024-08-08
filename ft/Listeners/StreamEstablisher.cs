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
        public EventHandler<StreamEstablishedEventArgs>? StreamEstablished;
        public abstract void Start();
        public abstract void Stop();
    }

    public class StreamEstablishedEventArgs(Stream stream, string destinationStr)
    {
        public Stream Stream { get; } = stream;
        public string DestinationEndpointString { get; } = destinationStr;
    }
}
