using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    public enum FlatOverlap { EntireStep, AtNextFoot, AtStart }

    [Test]
    [Arguments(0f, 0f, FlatOverlap.EntireStep, false, true)]
    [Arguments(.007f, 0f, FlatOverlap.EntireStep, false, true)]
    [Arguments(.007f, 0f, FlatOverlap.EntireStep, true, true)]
    [Arguments(.007f, .007f, FlatOverlap.EntireStep, false, true)]
    [Arguments(.007f, .003f, FlatOverlap.EntireStep, false, false)]
    [Arguments(.007f, 0f, FlatOverlap.AtNextFoot, false, true)]
    [Arguments(.007f, .007f, FlatOverlap.AtStart, false, false)]
    public async Task MoveTowards_LevelStepOnTwoFlatDecks_KeepsItsOriginalHeight(
        float separation, float startingOffset, FlatOverlap overlap, bool reverseOrder, bool shouldMove)
    {
        Vector3[] lower = [new(100, 98, DeckZ), new(121, 98, DeckZ), new(110, 108, DeckZ)];
        Vector3[] footprint = overlap switch
        {
            FlatOverlap.AtNextFoot => [new(111.15f, 99, DeckZ), new(113, 99, DeckZ), new(111.4f, 102, DeckZ)],
            FlatOverlap.AtStart => [new(110, 99, DeckZ), new(111.25f, 99, DeckZ), new(111, 102, DeckZ)],
            _ => lower
        };
        var upper = footprint.Select(vertex => vertex + Vector3.UnitZ * separation).ToArray();
        Vector3[] vertices = reverseOrder ? [.. upper, .. lower] : [.. lower, .. upper];
        var npc = CreateDeckWalker(customMesh: new CryTriangleMesh(vertices, [0, 1, 2, 3, 4, 5], []));
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        var original = new Vector3(111, 100, DeckZ + startingOffset);
        npc.Transform.Local.SetPosition(original);
        var target = original + Vector3.UnitX * 2f;

        var preview = NpcStepSurfaceTrace.DescribeStep(npc, target, Step).ToArray();
        npc.MoveTowards(target, Step, rangeTolerance: 0f);

        var expected = shouldMove ? original + Vector3.UnitX * Step : original;
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(expected);
        await Assert.That(npc.Movements.Count).IsEqualTo(shouldMove ? 1 : 0);
        if (!shouldMove)
            await Assert.That(preview[1]).Contains("AmbiguousLevelFoot");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task MoveTowards_CoplanarPatchAtEitherFoot_UsesContinuousFaceInEitherOrder(bool atEnd, bool patchFirst)
    {
        Vector3[] floor = [new(100, 98, DeckZ), new(121, 98, DeckZ), new(110, 108, DeckZ)];
        Vector3[] patch = atEnd
            ? [new(111.15f, 99, DeckZ), new(113, 99, DeckZ), new(111.4f, 102, DeckZ)]
            : [new(110, 99, DeckZ), new(111.25f, 99, DeckZ), new(111, 102, DeckZ)];
        var upper = floor.Select(vertex => vertex + Vector3.UnitZ * .007f);
        Vector3[] vertices = patchFirst ? [.. patch, .. floor, .. upper] : [.. floor, .. patch, .. upper];
        var mesh = new CryTriangleMesh(vertices, [0, 1, 2, 3, 4, 5, 6, 7, 8], []);
        var npc = CreateDeckWalker(customMesh: mesh);
        npc.ParentWorld.Template.ZoneBaiLoader.Clear();
        var from = new Vector3(111, 100, DeckZ);
        npc.Transform.Local.SetPosition(from);

        npc.MoveTowards(from + Vector3.UnitX * 2f, Step, rangeTolerance: 0f);

        await Assert.That(npc.Transform.Local.Position).IsEqualTo(from + Vector3.UnitX * Step);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }
}
