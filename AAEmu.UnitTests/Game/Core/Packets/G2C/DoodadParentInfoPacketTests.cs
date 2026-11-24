using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public sealed class DoodadParentInfoPacketTests
{
    [Test, NotInParallel]
    public async Task HouseChildren_KeepFurnitureAndBindingsLocalAndSendGardenItemsInWorldSpace()
    {
        var data = new HousingGameData();
        var original = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = original.GetValue(null);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(HousingGameData).GetField("_housingDecorations", fields)!.SetValue(data,
            new Dictionary<uint, HousingDecoration> { [104] = new() { Id = 104, DoodadId = 2142 } });
        typeof(HousingGameData).GetField("_housingItemHousingDecorations", fields)!.SetValue(data,
            new List<ItemHousingDecoration> { new() { ItemId = 15243, DesignId = 104 } });
        original.SetValue(null, data);
        try
        {
            var house = new House { Id = 3, ObjId = 172 };
            house.Transform.Local.Position = new Vector3(1000, 2000, 30);
            house.Transform.Local.SetZRotation(MathF.PI / 2);
            var children = new (uint Template, uint Item, AttachPointKind Socket, bool Local)[]
            {
                (2142, 15243, AttachPointKind.None, true),  // Furniture item for this model.
                (2142, 0, AttachPointKind.None, true),      // Older furniture without a saved source.
                (2142, 15182, AttachPointKind.None, false), // Normal placement item with the same model.
                (2261, 15661, AttachPointKind.None, false), // Carrot seed.
                (3054, 17683, AttachPointKind.None, false), // Dropped Lumber Pack.
                (4278, 0, AttachPointKind.HealPoint0, true), // Cottage door at socket 36.
                (4322, 0, AttachPointKind.HealPoint1, true)  // Cottage window at socket 37.
            };
            foreach (var (template, item, socket, local) in children)
            {
                var child = new Doodad { ObjId = 9, TemplateId = template, ItemTemplateId = item, AttachPoint = socket,
                    ParentObj = house, ParentObjId = house.ObjId, OwnerType = DoodadOwnerType.Housing, OwnerDbId = house.Id };
                child.Transform.Parent = house.Transform;
                child.Transform.Local.Position = new Vector3(2, 3, 1);
                child.Transform.Local.SetZRotation(0.1f);
                var expected = local ? child.Transform.Local : child.Transform.World;
                foreach (var batch in new[] { false, true })
                {
                    var bytes = batch ? new SCDoodadsCreatedPacket([child]).Write(new PacketStream()).GetBytes()
                        : new SCDoodadCreatedPacket(child).Write(new PacketStream()).GetBytes();
                    var body = new PacketStream(bytes);
                    if (batch)
                        await Assert.That(body.ReadByte()).IsEqualTo((byte)1);
                    body.ReadBc();
                    body.ReadUInt32();
                    body.ReadBc();
                    await Assert.That(body.ReadBc()).IsEqualTo(local ? house.ObjId : 0u);
                    await Assert.That(body.ReadByte()).IsEqualTo((byte)socket);
                    var (x, y, z) = body.ReadPosition();
                    await Assert.That(Vector3.Distance(new(x, y, z), expected.Position)).IsLessThan(0.01f);
                    var rotation = (body.ReadInt16(), body.ReadInt16(), body.ReadInt16());
                    await Assert.That(rotation).IsEqualTo(expected.ToRollPitchYawShorts());
                    await Assert.That(BitConverter.ToUInt32(bytes, bytes.Length - 8)).IsEqualTo(house.Id);
                }
                await Assert.That(child.ParentObjId).IsEqualTo(house.ObjId);
                await Assert.That(child.Transform.Parent).IsSameReferenceAs(house.Transform);
                await Assert.That(child.Transform.Local.Position).IsEqualTo(new Vector3(2, 3, 1));
            }
        }
        finally
        {
            original.SetValue(null, previous);
        }
    }

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
