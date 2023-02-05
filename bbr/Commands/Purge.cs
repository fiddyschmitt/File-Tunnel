using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class Purge : Command
    {
        public Purge(string connectionId) : base(connectionId)
        {
        }

        public override string Serialize()
        {
            var result = $"$purge|{ConnectionId}";
            return result;
        }
    }
}
