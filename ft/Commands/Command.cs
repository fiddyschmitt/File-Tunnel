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
            var messageStartPos = writer.BaseStream.Position;

            writer.BaseStream.WriteByte(0);    //command not ready

            PacketNumber = ++SentPacketCount;   //start at 1, because the Ack Reader will first read 0 from file
            writer.Write(PacketNumber);

            Serialize(writer);

            var messageEndPos = writer.BaseStream.Position;

            writer.BaseStream.WriteByte(0);  //command not ready (the following command)

            writer.BaseStream.Flush();

            //finally write the CommandId, signalling that the command is ready
            writer.BaseStream.Seek(messageStartPos, SeekOrigin.Begin);
            writer.Write(CommandId);
            writer.BaseStream.Flush();

            writer.BaseStream.Seek(messageEndPos, SeekOrigin.Begin);
        }

        public static Command? Deserialise(BinaryReader reader)
        {
            var commandId = reader.ReadByte();

            Command? result = commandId switch
            {
                Connect.COMMAND_ID => new Connect(),
                Forward.COMMAND_ID => new Forward(),
                Purge.COMMAND_ID => new Purge(),
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
