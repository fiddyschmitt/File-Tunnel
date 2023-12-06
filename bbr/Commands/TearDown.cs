using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class TearDown : Command
    {
        public const int COMMAND_ID = 4;
        public override int CommandId => COMMAND_ID;

        public int ConnectionId { get; protected set; }

        public TearDown() { }

        public TearDown(int connectionId)
        {
            ConnectionId = connectionId;
        }

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(ConnectionId);
        }

        protected override void Deserialize(BinaryReader reader)
        {
            ConnectionId = reader.ReadInt32();
        }
    }
}
