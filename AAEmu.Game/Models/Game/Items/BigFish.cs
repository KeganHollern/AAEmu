using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Items.Templates;

namespace AAEmu.Game.Models.Game.Items;

public class BigFish : Item
{
    public override ItemDetailType DetailType => ItemDetailType.BigFish;
    public override uint DetailBytesLength => 16;

    private DateTime? _captureDate;

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
        // The separate item creation column can round up to the next second.
        var captureDate = stream.ReadDateTime();
        _captureDate = captureDate > DateTime.UnixEpoch && captureDate < DateTime.MaxValue ? captureDate : null;
    }

    public override void WriteDetails(PacketStream stream)
    {
        stream.Write(Weight);     // Weight
        stream.Write(Length);     // Length
        stream.Write(_captureDate ?? CreateTime);
    }
}
