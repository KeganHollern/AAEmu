using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCUnitStatePacketTests
{
    [Test]
    public async Task Write_NpcWithDifferentReferenceHeight_PreservesAndSerializesCurrentTransform()
    {
        var npc = new Npc
        {
            ObjId = 1,
            TemplateId = 2,
            Template = new NpcTemplate { Scale = 1f },
            CanFly = true,
            Spawner = new NpcSpawner
            {
                Position = new WorldSpawnPosition { X = 1000.25f, Y = 2000.5f, Z = 75f }
            }
        };
        npc.Ai = new DummyAiCharacter { Owner = npc };
        npc.Transform.Local.SetPosition(1000.25f, 2000.5f, 123.75f);
        var expectedPosition = npc.Transform.Local.Position;

        var stream = new SCUnitStatePacket(npc).Write(new PacketStream());

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(expectedPosition);

        stream.Rollback();
        stream.ReadBc();
        stream.ReadString();
        stream.ReadByte();
        stream.ReadBc();
        stream.ReadUInt32();
        stream.ReadUInt32();
        stream.ReadByte();
        stream.ReadString();
        var (x, y, z) = stream.ReadPosition();

        await Assert.That(x).IsGreaterThanOrEqualTo(expectedPosition.X - 0.01f).And.IsLessThanOrEqualTo(expectedPosition.X + 0.01f);
        await Assert.That(y).IsGreaterThanOrEqualTo(expectedPosition.Y - 0.01f).And.IsLessThanOrEqualTo(expectedPosition.Y + 0.01f);
        await Assert.That(z).IsGreaterThanOrEqualTo(expectedPosition.Z - 0.01f).And.IsLessThanOrEqualTo(expectedPosition.Z + 0.01f);
    }
}
