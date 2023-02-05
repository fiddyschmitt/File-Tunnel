using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public abstract class Command
    {
        public Command(string connectionId)
        {
            ConnectionId = connectionId;
        }

        public string ConnectionId { get; }

        public abstract string Serialize();
    }
}
