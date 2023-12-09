﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr.Commands
{
    public class Forward : Command
    {
        public const int COMMAND_ID = 2;
        public override int CommandId => COMMAND_ID;

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
            Payload = new byte[expectedPayloadLength];

            var totalRead = 0;
            do
            {
                var remaining = expectedPayloadLength - totalRead;
                var read = reader.Read(Payload, totalRead, remaining);
                totalRead += read;
            }
            while (totalRead < expectedPayloadLength);
        }
    }
}

