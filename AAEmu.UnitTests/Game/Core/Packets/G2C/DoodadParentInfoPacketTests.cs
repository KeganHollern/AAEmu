using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class DoodadParentInfoPacketTests
{
    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(true, false)]
    public async Task Create_CarriesTheParentOrHouseOwnerForTheClientInfoAction(bool batch, bool parented)
    {
        var house = new BaseUnit { ObjId = 0x010203 };
        house.Transform.Local.Position = new Vector3(1000, 2000, 30);
        var sign = new Doodad
        {
            ObjId = 0xabcdef,
            TemplateId = 3400,
            ParentObjId = parented ? house.ObjId : 0,
            ParentObj = parented ? house : null,
            AttachPoint = AttachPointKind.None,
            OwnerId = 7,
            OwnerType = DoodadOwnerType.Housing,
            OwnerDbId = 1234,
            PlantTime = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc)
        };
        typeof(Doodad).GetField("_funcGroupId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sign, 8136u);
        sign.CurrentFuncs.Add(new DoodadFunc { FuncType = nameof(DoodadFuncParentInfo), SkillId = 15212 });
        if (parented)
            sign.Transform.Parent = house.Transform;
        sign.Transform.Local.Position = new Vector3(1, 2, 3);

        var bytes = batch
            ? new SCDoodadsCreatedPacket([sign]).Write(new PacketStream()).GetBytes()
            : new SCDoodadCreatedPacket(sign).Write(new PacketStream()).GetBytes();
        var body = new PacketStream(bytes);
        if (batch)
            await Assert.That(body.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(body.ReadBc()).IsEqualTo(0xabcdefu);
        await Assert.That(body.ReadUInt32()).IsEqualTo(3400u);
        await Assert.That(body.ReadBc()).IsEqualTo(0u); // Owner object differs from parent object.
        await Assert.That(body.ReadBc()).IsEqualTo(parented ? house.ObjId : 0u);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)0); // Parent-only attachments also use local position.
        var (x, y, z) = body.ReadPosition();
        await Assert.That(x).IsEqualTo(1f);
        await Assert.That(y).IsEqualTo(2f);
        await Assert.That(MathF.Abs(z - 3f)).IsLessThan(0.001f);
        await Assert.That(body.ReadInt16()).IsEqualTo((short)0);
        await Assert.That(body.ReadInt16()).IsEqualTo((short)0);
        await Assert.That(body.ReadInt16()).IsEqualTo((short)0);
        await Assert.That(body.ReadSingle()).IsEqualTo(sign.Scale);
        await Assert.That(body.ReadBoolean()).IsFalse(); // Keep the normal interaction wheel.
        await Assert.That(body.ReadUInt32()).IsEqualTo(8136u);
        await Assert.That(body.ReadUInt32()).IsEqualTo(7u);
        await Assert.That(body.ReadUInt64()).IsEqualTo(0ul);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(body.ReadDateTime()).IsEqualTo(sign.PlantTime);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0u);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.ReadInt32()).IsEqualTo(-1);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)DoodadOwnerType.Housing);
        await Assert.That(body.ReadUInt32()).IsEqualTo(1234u);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }
}
