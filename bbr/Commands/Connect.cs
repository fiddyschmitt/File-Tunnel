using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class Connect : Command
    {
        public Connect(string connectionId) : base(connectionId)
        {
        }

        public override string Serialize()
        {
            var result = $"$connect|{ConnectionId}";
            return result;
        }
    }
}
