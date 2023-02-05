using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class TearDown : Command
    {
        public TearDown(string connectionId) : base(connectionId)
        {
        }

        public override string Serialize()
        {
            var result = $"$teardown|{ConnectionId}";
            return result;
        }
    }
}
