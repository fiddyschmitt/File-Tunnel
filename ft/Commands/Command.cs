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
        public ulong PacketNumber { get; set; } = (ulong)Random.Shared.NextInt64();
        public uint CRC { get; set; }

        protected abstract void Deserialize(BinaryReader reader);
        protected abstract void Serialize(BinaryWriter writer);

        public void Serialise(BinaryWriter writer)
        {
            if (writer.BaseStream is not HashingStream hashingStream) return;
            hashingStream.StartHashing();

            writer.Write(CommandId);

            //PacketNumber = SentPacketCount++;
            writer.Write(PacketNumber);

            Serialize(writer);

            hashingStream.StopHashing();

            CRC = hashingStream.GetCrc32();
            writer.Write(CRC);

            //Don't flush here. This way, we can write multiple commands to the file in one go.
            //writer.Flush();
        }

        public static Command? Deserialise(BinaryReader reader)
        {
            if (reader.BaseStream is not HashingStream hashingStream) return null;
            hashingStream.StartHashing();

            var commandId = reader.ReadByte();

            Command? result = commandId switch
            {
                Connect.COMMAND_ID => new Connect(),
                CreateListener.COMMAND_ID => new CreateListener(),
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

                hashingStream.StopHashing();

                //check the crc
                var actualCrc = hashingStream.GetCrc32();
                result.CRC = reader.ReadUInt32();

                if (actualCrc != result.CRC)
                {
                    throw new InvalidDataException("Command is invalid (CRC mismatch).");
                }
            }

            return result;
        }
    }
}
