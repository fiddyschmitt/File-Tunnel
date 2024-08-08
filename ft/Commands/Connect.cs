using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Commands
{
    public class Connect : Command
    {
        public const byte COMMAND_ID = 10;
        public override byte CommandId => COMMAND_ID;

        public int ConnectionId { get; protected set; }
        public string DestinationEndpointString { get; protected set; }

        public Connect()
        {
            DestinationEndpointString = string.Empty;
        }

        public Connect(int connectionId, string destinationEndpointStr)
        {
            ConnectionId = connectionId;
            DestinationEndpointString = destinationEndpointStr;
        }
        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(ConnectionId);
            writer.Write(DestinationEndpointString);
        }

        protected override void Deserialize(BinaryReader reader)
        {
            ConnectionId = reader.ReadInt32();
            DestinationEndpointString = reader.ReadString();
        }
    }
}
