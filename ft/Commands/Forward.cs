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

        // Upper bound on a Forward payload. The write side (Relay) never emits more than its 65535-byte
        // buffer per Forward, so this generous ceiling only ever rejects a corrupt/hostile length while
        // bounding the allocation - a raw int32 read straight from the file could otherwise demand ~2GB.
        public const int MAX_PAYLOAD_LENGTH = 16 * 1024 * 1024;

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

            // The length is read straight from a (possibly torn or corrupt) file, before the CRC is checked.
            // Guard it: a negative or absurdly large value is corruption/desync - reject it rather than
            // attempting a negative or multi-GB allocation. InvalidDataException joins the pump's restart path.
            if (expectedPayloadLength < 0 || expectedPayloadLength > MAX_PAYLOAD_LENGTH)
            {
                throw new InvalidDataException($"Forward payload length {expectedPayloadLength} is out of range (0..{MAX_PAYLOAD_LENGTH}).");
            }

            Payload = new byte[expectedPayloadLength];

            var totalRead = 0;
            while (totalRead < expectedPayloadLength)
            {
                var read = reader.Read(Payload, totalRead, expectedPayloadLength - totalRead);

                // Read returns 0 at end-of-data (it does NOT throw). Without this guard a torn read that
                // exposes the header but not the whole payload spins here forever at 100% CPU. Throwing
                // EndOfStreamException routes it into the pump's existing "await resend / retry" handling,
                // exactly as a truncated fixed-size field already does.
                if (read == 0)
                {
                    throw new EndOfStreamException($"Forward payload truncated: read {totalRead} of {expectedPayloadLength} bytes.");
                }

                totalRead += read;
            }
        }
    }
}

