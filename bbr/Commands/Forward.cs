using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class Forward : Command
    {
        public Forward(string connectionId, byte[] payload) : base(connectionId)
        {
            Payload = payload;
        }

        public byte[] Payload { get; }

        public override string Serialize()
        {
            var payloadBase64 = Convert.ToBase64String(Payload);
            var result = $"$forward|{ConnectionId}|{payloadBase64}";
            return result;
        }
    }
}
