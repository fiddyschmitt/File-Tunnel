using System.IO;

namespace ft.Commands;

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