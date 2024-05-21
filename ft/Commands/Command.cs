using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Commands
{
    public abstract class Command
    {
        public Command()
        {

        }

        public abstract byte CommandId { get; }
        public static ulong SentPacketCount { get; set; }
        public ulong PacketNumber { get; set; }

        protected abstract void Deserialize(BinaryReader reader);
        protected abstract void Serialize(BinaryWriter writer);

        public void Serialise(BinaryWriter writer)
        {
            writer.Write(CommandId);

            PacketNumber = ++SentPacketCount;   //start at 1, because the Ack Reader will first read 0 from file
            writer.Write(PacketNumber);

            Serialize(writer);
        }

        public static Command? Deserialise(BinaryReader reader)
        {
            var commandId = reader.ReadByte();

            Command? result = commandId switch
            {
                Connect.COMMAND_ID => new Connect(),
                Forward.COMMAND_ID => new Forward(),
                TearDown.COMMAND_ID => new TearDown(),
                _ => null
            };

            if (result != null)
            {
                result.PacketNumber = reader.ReadUInt64();
                result.Deserialize(reader);
            }

            return result;
        }
    }
}
