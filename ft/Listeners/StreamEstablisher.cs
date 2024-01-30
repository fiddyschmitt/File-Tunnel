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
        public EventHandler<Stream>? StreamEstablished;
        public abstract void Stop();
    }
}
