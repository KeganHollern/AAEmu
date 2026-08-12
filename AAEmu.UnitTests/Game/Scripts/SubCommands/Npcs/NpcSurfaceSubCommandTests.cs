using System.Numerics;

using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Scripts.SubCommands.Npcs;

namespace AAEmu.UnitTests.Game.Scripts.SubCommands.Npcs;

public class NpcSurfaceSubCommandTests
{
    [Test]
    public async Task BuildReport_FormatsRuntimeAndSurfaceDeltasWithoutChangingState()
    {
        var npc = new Npc
        {
            ObjId = 7,
            TemplateId = 13,
            CanFly = false,
            Spawner = new NpcSpawner
            {
                Id = 17,
                SpawnerId = 19,
                Position = new WorldSpawnPosition { Z = 4f }
            }
        };
        npc.Ai = new DummyAiCharacter
        {
            Owner = npc,
            HomePosition = new Vector3(1f, 2f, 4.5f),
            IdlePosition = new Vector3(1f, 2f, 4.75f)
        };
        var local = new Vector3(1.25f, 2.5f, 5.5f);
        var world = new Vector3(11.25f, 12.5f, 5.5f);

        var report = NpcSurfaceSubCommand.BuildReport(npc, local, world, 23, 29, 5f, 6f);

        await Assert.That(report[0]).IsEqualTo("obj=7 template=13 spawner=17/19 instance=23 zone=29 canFly=False behavior=none");
        await Assert.That(report[1]).IsEqualTo("packetLocal=(1.250,2.500,5.500) queryWorld=(11.250,12.500,5.500)");
        await Assert.That(report[2]).IsEqualTo("authored=4.000 dZ=1.500 home=4.500 dZ=1.000 idle=4.750 dZ=0.750");
        await Assert.That(report[3]).IsEqualTo("terrain=5.000 dZ=0.500 legacyGeo=6.000 dZ=-0.500 geoMinusTerrain=1.000");
        await Assert.That(npc.Spawner.Position.Z).IsEqualTo(4f);
        await Assert.That(npc.Ai.HomePosition.Z).IsEqualTo(4.5f);
    }

    [Test]
    public async Task BuildReport_MissingSources_UsesExplicitUnavailableValues()
    {
        var npc = new Npc { ObjId = 1, TemplateId = 2 };

        var report = NpcSurfaceSubCommand.BuildReport(npc, Vector3.Zero, Vector3.Zero, 3, 4, null, null);

        await Assert.That(report[0]).Contains("spawner=n/a");
        await Assert.That(report[0]).Contains("behavior=no-ai");
        await Assert.That(report[2]).IsEqualTo("authored=n/a dZ=n/a home=n/a dZ=n/a idle=n/a dZ=n/a");
        await Assert.That(report[3]).IsEqualTo("terrain=n/a dZ=n/a legacyGeo=n/a dZ=n/a geoMinusTerrain=n/a");
    }
}
