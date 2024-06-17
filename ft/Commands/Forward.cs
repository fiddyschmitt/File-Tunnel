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
        public string? PayloadMD5 { get; protected set; }
        public byte[]? Payload { get; protected set; }

        public Forward() { }

        public Forward(int connectionId, byte[] payload)
        {
            ConnectionId = connectionId;
            PayloadMD5 = payload.GetMD5();
            Payload = payload;
        }

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(ConnectionId);
            writer.Write(PayloadMD5 ?? "");
            writer.Write(Payload?.Length ?? 0);

            if (Payload != null)
            {
                writer.Write(Payload);
            }
        }

        protected override void Deserialize(BinaryReader reader)
        {
            ConnectionId = reader.ReadInt32();
            PayloadMD5 = reader.ReadString();
            var expectedPayloadLength = reader.ReadInt32();
            Payload = new byte[expectedPayloadLength];

            var totalRead = 0;
            do
            {
                var remaining = expectedPayloadLength - totalRead;
                var read = reader.Read(Payload, totalRead, remaining);
                totalRead += read;
            }
            while (totalRead < expectedPayloadLength);

            if (PayloadMD5 != Payload.GetMD5())
            {
                if (Debugger.IsAttached)
                {
                    var payloadHex = string.Concat(Array.ConvertAll(Payload, x => x.ToString("X2")));
                    Program.Log($"{payloadHex}");
                }

                throw new Exception($"Payload is invalid.");
            }
        }
    }
}

