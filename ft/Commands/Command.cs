using ft.Streams;
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
            if (writer.BaseStream is not HashingStream hashingStream) return;
            hashingStream.StartHashing();

            writer.Write(CommandId);

            PacketNumber = SentPacketCount++;
            writer.Write(PacketNumber);

            Serialize(writer);

            hashingStream.StopHashing();

            var crc = hashingStream.GetCrc32();
            writer.Write(crc);

            writer.Flush();
        }

        public static Command? Deserialise(BinaryReader reader)
        {
            if (reader.BaseStream is not HashingStream hashingStream) return null;
            hashingStream.StartHashing();

            var commandId = reader.ReadByte();

            Command? result = commandId switch
            {
                Connect.COMMAND_ID => new Connect(),
                Forward.COMMAND_ID => new Forward(),
                Purge.COMMAND_ID => new Purge(),
                TearDown.COMMAND_ID => new TearDown(),
                Ping.COMMAND_ID => new Ping(),
                _ => null
            };

            if (result != null)
            {
                result.PacketNumber = reader.ReadUInt64();
                result.Deserialize(reader);
            }

            hashingStream.StopHashing();

            //check the crc
            var actualCrc = hashingStream.GetCrc32();
            var expectedCrc = reader.ReadUInt32();

            if (actualCrc != expectedCrc)
            {
                throw new Exception("Command is invalid.");
            }

            return result;
        }
    }
}
