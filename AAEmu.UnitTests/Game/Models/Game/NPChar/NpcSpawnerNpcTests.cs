using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

public class NpcSpawnerNpcTests
{
    [Test]
    public async Task CreateRuntimeSpawnPosition_TerrainGroundWithinTolerance_ChangesOnlyRuntimeCopy()
    {
        var authored = CreateAuthoredPosition();
        var authoredSnapshot = authored.Clone();

        var runtime = NpcSpawnerNpc.CreateRuntimeSpawnPosition(authored, TerrainSurface(authored.Z + 0.5f));

        await Assert.That(runtime).IsNotSameReferenceAs(authored);
        await Assert.That(runtime.Z).IsEqualTo(authored.Z + 0.5f);
        await AssertPositionMatches(runtime, authoredSnapshot, includeHeight: false);
        await AssertPositionMatches(authored, authoredSnapshot, includeHeight: true);
    }

    [Test]
    public async Task CreateRuntimeSpawnPosition_TerrainGroundAtToleranceBoundary_PreservesAuthoredHeight()
    {
        var authored = CreateAuthoredPosition();

        var runtime = NpcSpawnerNpc.CreateRuntimeSpawnPosition(authored, TerrainSurface(authored.Z + 1f));

        await Assert.That(runtime.Z).IsEqualTo(authored.Z);
        await Assert.That(authored.Z).IsEqualTo(30.75f);
    }

    [Test]
    public async Task CreateRuntimeSpawnPosition_NavigationNodeGround_PreservesAuthoredHeight()
    {
        // aaemu-cluster#92 (V11): indoor nav-node heights are coarse voxel samples; snapping spawn Z
        // onto them lifted the Sharpwind researchers off the floor and clients bounced them.
        var authored = CreateAuthoredPosition();

        var runtime = NpcSpawnerNpc.CreateRuntimeSpawnPosition(authored, new GroundSurfaceResult(
            authored.Z + 0.5f, GroundSurfaceSource.NavigationNode,
            GroundSurfaceDecision.NavigationHeightPreserved, GroundSurfaceFailure.None, null));

        await Assert.That(runtime.Z).IsEqualTo(authored.Z);
        await Assert.That(authored.Z).IsEqualTo(30.75f);
    }

    [Test]
    public async Task CreateRuntimeSpawnPosition_WithoutGroundSample_PreservesIndependentCopy()
    {
        var authored = CreateAuthoredPosition();

        var runtime = NpcSpawnerNpc.CreateRuntimeSpawnPosition(authored, null);
        runtime.Z += 10f;

        await Assert.That(runtime).IsNotSameReferenceAs(authored);
        await Assert.That(authored.Z).IsEqualTo(30.75f);
    }

    private static GroundSurfaceResult TerrainSurface(float height)
    {
        return new GroundSurfaceResult(height, GroundSurfaceSource.Terrain,
            GroundSurfaceDecision.TerrainOnly, GroundSurfaceFailure.None, null);
    }

    [Test]
    public async Task Clone_ReturnsSpawnerWithIndependentPosition()
    {
        var source = new NpcSpawner
        {
            Id = 7,
            SpawnerId = 11,
            UnitId = 13,
            Position = CreateAuthoredPosition()
        };

        var clone = NpcSpawner.Clone(source);
        clone.Position.Z += 10f;

        await Assert.That(clone).IsNotSameReferenceAs(source);
        await Assert.That(clone.Position).IsNotSameReferenceAs(source.Position);
        await Assert.That(clone.Id).IsEqualTo(source.Id);
        await Assert.That(clone.SpawnerId).IsEqualTo(source.SpawnerId);
        await Assert.That(clone.UnitId).IsEqualTo(source.UnitId);
        await Assert.That(source.Position.Z).IsEqualTo(30.75f);
    }

    private static WorldSpawnPosition CreateAuthoredPosition()
    {
        return new WorldSpawnPosition
        {
            WorldId = 1,
            ZoneId = 2,
            X = 10.25f,
            Y = 20.5f,
            Z = 30.75f,
            Yaw = 0.1f,
            Pitch = 0.2f,
            Roll = 0.3f
        };
    }

    private static async Task AssertPositionMatches(WorldSpawnPosition actual, WorldSpawnPosition expected, bool includeHeight)
    {
        await Assert.That(new Vector2(actual.X, actual.Y)).IsEqualTo(new Vector2(expected.X, expected.Y));
        if (includeHeight)
            await Assert.That(actual.Z).IsEqualTo(expected.Z);
        await Assert.That(actual.WorldId).IsEqualTo(expected.WorldId);
        await Assert.That(actual.ZoneId).IsEqualTo(expected.ZoneId);
        await Assert.That(actual.Yaw).IsEqualTo(expected.Yaw);
        await Assert.That(actual.Pitch).IsEqualTo(expected.Pitch);
        await Assert.That(actual.Roll).IsEqualTo(expected.Roll);
    }
}
