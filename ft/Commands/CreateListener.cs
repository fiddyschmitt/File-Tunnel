using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Commands
{
    public class CreateListener : Command
    {
        public const byte COMMAND_ID = 11;
        public override byte CommandId => COMMAND_ID;

        public string Protocol { get; protected set; }
        public string ForwardString { get; protected set; }

        public CreateListener()
        {
            Protocol = string.Empty;
            ForwardString = string.Empty;
        }

        public CreateListener(string protocol, string forwardStr)
        {
            Protocol = protocol;
            ForwardString = forwardStr;
        }

        protected override void Serialize(BinaryWriter writer)
        {
            writer.Write(Protocol);
            writer.Write(ForwardString);
        }

        protected override void Deserialize(BinaryReader reader)
        {
            Protocol = reader.ReadString();
            ForwardString = reader.ReadString();
        }
    }
}
