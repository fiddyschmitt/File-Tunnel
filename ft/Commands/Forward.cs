using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Commands
{
    public class Forward : Command
    {
        public const byte COMMAND_ID = 20;
        public override byte CommandId => COMMAND_ID;

        public int ConnectionId { get; protected set; }
        public byte[]? Payload { get; protected set; }

        public Forward() { }

        public Forward(int connectionId, byte[] payload)
        {
            ConnectionId = connectionId;
            Payload = payload;
        }

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(ConnectionId);
            writer.Write(Payload?.Length ?? 0);

            if (Payload != null)
            {
                writer.Write(Payload);
            }
        }

        protected override void Deserialize(BinaryReader reader)
        {
            ConnectionId = reader.ReadInt32();

            var expectedPayloadLength = reader.ReadInt32();

            var remainingInFile = reader.BaseStream.Length - reader.BaseStream.Position;
            if (expectedPayloadLength > remainingInFile)
            {
                throw new Exception($"[Packet {PacketNumber:N0}]: Payload length is {expectedPayloadLength:N0} bytes, which exceeds what remains in the file ({remainingInFile:N0} bytes). This is likely caused by reading stale content from the file.");
            }

            Payload = new byte[expectedPayloadLength];

            var totalRead = 0;
            do
            {
                var remaining = expectedPayloadLength - totalRead;
                var read = reader.Read(Payload, totalRead, remaining);

                if (read == 0 && reader.BaseStream.Position == reader.BaseStream.Length)
                {
                    throw new Exception($"[Packet {PacketNumber:N0}]: Attempted to read beyond the end of file.");
                }

                totalRead += read;
            }
            while (totalRead < expectedPayloadLength);
        }
    }
}

