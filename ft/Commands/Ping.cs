using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Commands
{
    public class Ping : Command
    {
        public const byte COMMAND_ID = 30;

        public EnumPingType PingType { get; protected set; }
        public ulong ResponseToPacketNumber { get; set; }

        public Ping()
        {

        }

        public Ping(EnumPingType pingType)
        {
            PingType = pingType;
        }

        public override byte CommandId => COMMAND_ID;

        

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write((int)PingType);
            writer.Write(ResponseToPacketNumber);
        }

        protected override void Deserialize(BinaryReader reader)
        {
            PingType = (EnumPingType)reader.ReadInt32();
            ResponseToPacketNumber = reader.ReadUInt64();
        }
    }

    public enum EnumPingType
    {
        Request,
        Response
    }
}
