using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items;

public class BigFish : Item
{
    public override ItemDetailType DetailType => ItemDetailType.BigFish;
    public override uint DetailBytesLength => 16;

    public float Weight { get; set; }
    public float Length { get; set; }

    public BigFish()
    {
    }

    public BigFish(ulong id, ItemTemplate template, int count) : base(id, template, count)
    {
    }

    public override void ReadDetails(PacketStream stream)
    {
        if (stream.LeftBytes < DetailBytesLength)
            return;
        Weight = stream.ReadSingle();
        Length = stream.ReadSingle();
        // The item record supplies CreateTime separately.
        stream.ReadDateTime();
    }

    public override void WriteDetails(PacketStream stream)
    {
        stream.Write(Weight);     // Weight
        stream.Write(Length);     // Length
        stream.Write(CreateTime); // Capture Date
    }
}
