using System.Globalization;
using System.Numerics;

using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Scripts.SubCommands.Npcs;
using AAEmu.Game.Utils.Scripts;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class WorldManagerHeightTests
{
    private static string[] SurfaceCommand(Npc npc, params string[] args)
    {
        var character = new CharacterMock { CurrentTarget = npc };
        var output = new CharacterMessageOutput(character);
        new NpcSurfaceSubCommand().PreExecute(character, "surface", args, output);
        return output.Messages.ToArray();
    }

    [Test]
    public async Task SurfaceCommand_DefaultKeepsPointReport_OptionalStepIdentifiesBothFacesWithoutMoving()
    {
        var npc = CreateDeckWalker();
        var from = new Vector3(111f, 100f, DeckZ);
        npc.Transform.Local.SetPosition(from);
        var point = SurfaceCommand(npc, "target");
        await Assert.That(point.Length).IsEqualTo(5);
        await Assert.That(point[1]).Contains("packetLocal=(111.000,100.000,6.000) queryWorld=(111.000,100.000,6.000)");
        await Assert.That(point[4]).Contains("selected=NavigationNode");

        var step = SurfaceCommand(npc, "target", "to=105,100,6.002");
        await Assert.That(step.Length).IsEqualTo(5);
        await Assert.That(step[0]).Contains("Prospective ad-hoc step height");
        await Assert.That(step[0]).Contains("from=(111,100,6) target=(105,100,6.002) requested=(110.5,100,6.000");
        await Assert.That(step[1]).Contains("selected=StaticFloor z=6.000 staticCheck=ContinuousStaticFloor");
        await Assert.That(step[2]).Contains("ownerCell=(0,0) placement=main_world/cells/000_000/client/object.dat#0");
        await Assert.That(step[3]).Contains("model=objects/deck.cgf");
        await Assert.That(step[4]).Contains("solidPart=0 startFace=0 endFace=1");
        await Assert.That(npc.Transform.Local.Position).IsEqualTo(from);
        await Assert.That(npc.Movements.Count).IsEqualTo(0);

        npc.MoveTowards(new Vector3(105f, 100f, 6.002f), Step, rangeTolerance: 0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(DeckZ);
    }

    [Test]
    [Arguments(110.5f, 99f, DeckZ, "DestinationOutsideBrush")]
    [Arguments(78.5f, 100f, 4f, "DestinationChangesLayer")]
    public async Task SurfaceCommand_EdgeOrRequestedLayerChange_ExplainsTerrainSelection(float x, float y, float z, string reason)
    {
        var npc = CreateDeckWalker();
        npc.Transform.Local.SetPosition(110.5f, 100f, DeckZ);

        var destination = string.Create(CultureInfo.InvariantCulture, $"to={x},{y},{z}");
        var report = SurfaceCommand(npc, "target", destination, "step=1");
        await Assert.That(report[1]).Contains($"selected=Terrain z=0.000 staticCheck={reason}");
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
        npc.MoveTowards(new Vector3(x, y, z), 1f, rangeTolerance: 0f);
        await Assert.That(npc.Movements.Single().Z).IsEqualTo(0f);
    }

    [Test]
    public async Task SurfaceCommand_AbsentModelNamesUnavailableGeometry()
    {
        var npc = CreateDeckWalker(overlappingModelReads: []);
        npc.ParentWorld.Template.Cells[0, 0].LoadedObjectDat.PrefabsList.RemoveAt(1);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);

        var report = SurfaceCommand(npc, "target", "to=105,100,6");
        await Assert.That(report[1]).Contains("selected=Terrain z=0.000 staticCheck=GeometryUnavailable");
        await Assert.That(report[3]).Contains("model=objects/missing.cgf");
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SurfaceCommand_GapBetweenFacesReportsFailedProof()
    {
        var npc = CreateDeckWalker(hasGap: true);
        npc.Transform.Local.SetPosition(111f, 100f, DeckZ);

        var report = SurfaceCommand(npc, "target", "to=105,100,6");
        await Assert.That(report[1]).Contains("selected=Terrain z=0.000 staticCheck=FacesDisconnectedAlongStep");
        await Assert.That(report[4]).Contains("solidPart=0 startFace=3 endFace=0");
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SurfaceCommand_InvalidCoordinatesReturnAnErrorWithoutMoving()
    {
        var npc = CreateDeckWalker();
        var report = SurfaceCommand(npc, "target", "to=105,100,NaN");

        await Assert.That(report.Single()).Contains("Supply three finite world coordinates");
        await Assert.That(npc.Movements.Count).IsEqualTo(0);
    }
}
