using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Objects;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    private static Vector3 CaveSlope(float x, float y) => new(x, y, DeckZ + .012f * (x - 110f));

    private MovementProbeNpc CreateCaveWalkerAtOverlap(bool separateParts = false)
    {
        // The second triangle starts at X=111.2, 7 mm above the first.
        var lower = new[] { CaveSlope(103, 95), CaveSlope(126, 95), CaveSlope(114, 110) };
        var upper = new[] { CaveSlope(111.2f, 95), CaveSlope(124, 95), CaveSlope(111.2f, 110) }
            .Select(p => p + Vector3.UnitZ * .007f);
        var npc = CreateDeckWalker(customMesh: new CryTriangleMesh([.. lower, .. upper], [0, 1, 2, 3, 4, 5], []));
        if (separateParts)
        {
            var load = npc.ParentWorld.Template.GeoData.GroundBrushGeometry;
            var stock = load(new CryWorldObjectInstance("objects/deck.cgf", Matrix4x4.Identity, default, default, ""));
            var original = stock.Parts[0];
            var twoParts = stock with { Parts =
            [
                original with { Shape = new CryTriangleMesh(lower, [0, 1, 2], []) },
                original with { Shape = new CryTriangleMesh(upper.ToArray(), [0, 1, 2], []) }
            ] };
            npc.ParentWorld.Template.GeoData = new AiGeoDataManager(npc.ParentWorld.Template)
                { GroundBrushGeometry = _ => twoParts };
        }
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        SetTerrainHeight(npc.ParentWorld.Template, 18);
        npc.Template = new NpcTemplate { Id = 2582, ModelId = 329, Scale = 1f };
        npc.Transform.Local.SetPosition(CaveSlope(110, 100));
        return npc;
    }

    [Test]
    [Arguments(0f)]
    [Arguments(.00015f)]
    [Arguments(-.00015f)]
    public async Task MoveTowards_EntersOverlapFromTwoStepsOnOneInclinedFace_FollowsTheSameFace(float targetOffset)
    {
        var npc = CreateCaveWalkerAtOverlap();
        var target = CaveSlope(115, 100) + Vector3.UnitZ * targetOffset;
        for (var i = 1; i <= 5; i++)
        {
            npc.MoveTowards(target, Step, rangeTolerance: 0);
            var foot = npc.Transform.Local.Position;
            await Assert.That(foot.X).IsEqualTo(110f + i * Step).Within(.001f);
            await Assert.That(foot.Z).IsEqualTo(CaveSlope(foot.X, foot.Y).Z).Within(.00001f);
            await Assert.That(npc.Movements.Count).IsEqualTo(i);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MoveTowards_AlternateTargetOnOverlappingDeck_ResumesOnLastProvenFace(bool levelStep)
    {
        var npc = CreateCaveWalkerAtOverlap(separateParts: true);
        var target = CaveSlope(115, 100);
        for (var i = 0; i < 3; i++)
            npc.MoveTowards(target, Step, rangeTolerance: 0);
        var from = npc.Transform.Local.Position;
        var alternate = (levelStep ? CaveSlope(111.7f, 100) : target) + Vector3.UnitZ * .007f;
        npc.MoveTowards(alternate, Step, rangeTolerance: 0);
        var actual = npc.Transform.Local.Position;
        if (levelStep)
        {
            // Normal level grounding selected the same inclined triangle for this short step.
            await Assert.That(actual.X).IsGreaterThan(from.X);
            await Assert.That(actual.Z).IsEqualTo(CaveSlope(actual.X, actual.Y).Z).Within(.00001f);
        }
        else
            await Assert.That(actual).IsEqualTo(from);

        for (var retry = 1; retry <= 2; retry++)
        {
            npc.MoveTowards(target, Step, rangeTolerance: 0);
            var next = npc.Transform.Local.Position;
            await Assert.That(next.X).IsEqualTo(actual.X + retry * Step).Within(.001f);
            await Assert.That(next.Z).IsEqualTo(CaveSlope(next.X, next.Y).Z).Within(.00001f);
        }
    }

    public enum LostFace { World, CellData, SolidPart, AuthoredMove, Displacement, ActualDescent }

    [Test]
    [Arguments(LostFace.World)]
    [Arguments(LostFace.CellData)]
    [Arguments(LostFace.SolidPart)]
    [Arguments(LostFace.AuthoredMove)]
    [Arguments(LostFace.Displacement)]
    [Arguments(LostFace.ActualDescent)]
    public async Task MoveTowards_OriginalInclinedFaceAfterRelocationOrReload_HoldsWithoutFreshEvidence(LostFace change)
    {
        var npc = CreateCaveWalkerAtOverlap();
        var template = npc.ParentWorld.Template;
        var load = template.GeoData.GroundBrushGeometry;
        var changedPart = false;
        if (change == LostFace.SolidPart)
        {
            template.GeoData = new AiGeoDataManager(template)
            {
                GroundBrushGeometry = brush =>
                {
                    var asset = load(brush);
                    return changedPart ? asset with { Parts = [asset.Parts[0] with { }] } : asset;
                }
            };
        }
        var target = CaveSlope(115, 100);
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        var original = npc.Transform.Local.Position;
        var world = npc.ParentWorld;
        switch (change)
        {
            case LostFace.World:
                npc.ParentWorld = new WorldInstance(world.Template, 1, true, world.Id);
                npc.ParentWorld = world;
                break;
            case LostFace.SolidPart:
                changedPart = true;
                break;
            case LostFace.CellData:
                var cell = world.Template.Cells[0, 0];
                var objects = cell.LoadedObjectDat;
                cell.LoadedObjectDat = new ObjectsFile("replacement")
                {
                    AssetPathsList = objects.AssetPathsList, PrefabsList = objects.PrefabsList
                };
                break;
            case LostFace.AuthoredMove:
                npc.MoveTowards(CaveSlope(111.3f, 100), Step, rangeTolerance: 0, authoredPathZ: true);
                break;
            case LostFace.Displacement:
                // The normal teleport/impulse caller reports its actual displacement.
                var away = original + 3 * Vector3.UnitY;
                npc.Transform.Local.SetPosition(away);
                npc.CheckMovedPosition(original);
                npc.Transform.Local.SetPosition(original);
                npc.CheckMovedPosition(away);
                break;
            case LostFace.ActualDescent:
                SetTerrainHeight(world.Template, 0);
                npc.MoveTowards(new Vector3(111.5f, 100, 0), Step, rangeTolerance: 0);
                await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(0f);
                SetTerrainHeight(world.Template, 18);
                npc.Transform.Local.SetPosition(original);
                break;
        }
        var before = npc.Transform.Local.Position;
        var sent = npc.Movements.Count;
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(before);
        await Assert.That(npc.Movements.Count).IsEqualTo(sent);
        await Assert.That(npc.AcceptedFloor).IsNull();
    }

    [Test]
    public async Task DescribeStep_OnInclinedFace_ChangesNeitherColdNorAcceptedMovementState()
    {
        var npc = CreateCaveWalkerAtOverlap();
        var target = CaveSlope(115, 100);
        _ = NpcStepSurfaceTrace.DescribeStep(npc, target, Step).ToArray();
        await Assert.That(npc.AcceptedFloor).IsNull();
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        var from = npc.Transform.Local.Position;
        var floor = npc.AcceptedFloor;
        var trace = NpcStepSurfaceTrace.DescribeStep(npc, target, Step).ToArray();
        await Assert.That(trace[1]).Contains("staticCheck=ContinuedInclinedFace");
        _ = NpcStepSurfaceTrace.DescribeStep(npc, from + Vector3.UnitZ * 2, Step).ToArray();
        await Assert.That(ReferenceEquals(floor, npc.AcceptedFloor)).IsTrue();
        await Assert.That(floor.Foot).IsEqualTo(from);
        npc.MoveTowards(target, Step, rangeTolerance: 0);
        await Assert.That(npc.Transform.Local.Position.X).IsGreaterThan(from.X);
    }
}
