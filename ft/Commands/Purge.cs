using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Commands
{
    public class Purge : Command
    {
        public const byte COMMAND_ID = 40;
        public override byte CommandId => COMMAND_ID;

        public Purge()
        {

        }

        protected override void Serialize(BinaryWriter writer)
        {

        }

        protected override void Deserialize(BinaryReader reader)
        {

        }
    }
}
