using System.IO;

namespace ft.Commands
{
    // Sent by the side that dials the destination (the SOCKS "exit") back to the side hosting the SOCKS
    // proxy, so it can return an accurate SOCKS reply code to its client. ft otherwise has no positive
    // per-connection "dial succeeded/failed" signal. Non-SOCKS -L/-R connections also receive one and
    // simply discard it (they register no waiter). Status is a ft.Socks.ConnectStatus value.
    public class ConnectResult : Command
    {
        public const byte COMMAND_ID = 12;
        public override byte CommandId => COMMAND_ID;

        public int ConnectionId { get; protected set; }
        public byte Status { get; protected set; }

        public ConnectResult() { }

        public ConnectResult(int connectionId, byte status)
        {
            ConnectionId = connectionId;
            Status = status;
        }

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(ConnectionId);
            writer.Write(Status);
        }

        protected override void Deserialize(BinaryReader reader)
        {
            ConnectionId = reader.ReadInt32();
            Status = reader.ReadByte();
        }
    }
}
