using System.Drawing;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Scripts.Commands;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.UnitTests.Game.Scripts.Commands;

public class SurfaceCommandTests
{
    [Test]
    public async Task SurfaceCommands_SeaLevelGround_DisplaysAndUsesValidZero()
    {
        var template = CreateWorldTemplate(0);
        var surface = CommandSurfaceResult.Resolve(template, new Vector3(0.5f, 0.5f, 17f));
        var markerPosition = new WorldSpawnPosition { X = 0.5f, Y = 0.5f, Z = 17f };

        var destinationResolved = Teleport.TryBuildPingDestination(surface, out var destination);
        var markerResolved = TestHeight.TryResolveMarkerPosition(template, markerPosition, out _);
        var heightReport = Height.BuildReport("Target", surface);
        var pingReport = PingPosition.BuildReport(surface);

        await Assert.That(surface.TerrainHeight).IsEqualTo(0f);
        await Assert.That(surface.SelectedGround.IsResolved).IsTrue();
        await Assert.That(surface.SelectedGround.Height).IsEqualTo(0f);
        await Assert.That(surface.SelectedGround.Source).IsEqualTo(GroundSurfaceSource.Terrain);
        await Assert.That(surface.LegacyHeight).IsEqualTo(0f);
        await Assert.That(destinationResolved).IsTrue();
        await Assert.That(destination).IsEqualTo(new Vector3(0.5f, 0.5f, 2.5f));
        await Assert.That(markerResolved).IsTrue();
        await Assert.That(markerPosition.Z).IsEqualTo(0f);
        await Assert.That(heightReport).Contains("Target Z-Pos: 17.000");
        await Assert.That(heightReport).Contains("terrain=0.000 selectedGround=0.000 source=Terrain");
        await Assert.That(heightReport).Contains("legacyHeight=0.000");
        await Assert.That(pingReport).Contains("referenceZ:17.000");
        await Assert.That(pingReport).Contains("selectedGround=0.000 source=Terrain");
    }

    [Test]
    public async Task SurfaceCommands_NegativeLayeredGround_UsesSelectedWaypointHeight()
    {
        var template = CreateWorldTemplate(50);
        AddWaypointBai(template, new Vector3(0.5f, 0.5f, -4f));
        var surface = CommandSurfaceResult.Resolve(template, new Vector3(0.5f, 0.5f, -3f));
        var markerPosition = new WorldSpawnPosition { X = 0.5f, Y = 0.5f, Z = -3f };

        var destinationResolved = Teleport.TryBuildPingDestination(surface, out var destination);
        var markerResolved = TestHeight.TryResolveMarkerPosition(template, markerPosition, out var markerSurface);

        await Assert.That(surface.TerrainHeight).IsEqualTo(50f);
        await Assert.That(surface.SelectedGround.IsResolved).IsTrue();
        await Assert.That(surface.SelectedGround.Height).IsEqualTo(-4f);
        await Assert.That(surface.SelectedGround.Source).IsEqualTo(GroundSurfaceSource.NavigationNode);
        await Assert.That(surface.LegacyHeight).IsEqualTo(-4f);
        await Assert.That(destinationResolved).IsTrue();
        await Assert.That(destination).IsEqualTo(new Vector3(0.5f, 0.5f, -1.5f));
        await Assert.That(markerResolved).IsTrue();
        await Assert.That(markerSurface.QueryPosition).IsEqualTo(new Vector3(0.5f, 0.5f, -3f));
        await Assert.That(markerPosition.Z).IsEqualTo(-4f);
    }

    [Test]
    public async Task PingPosition_LayeredGround_UsesClientPingReferenceHeight()
    {
        var template = CreateWorldTemplate(50);
        AddWaypointBai(template,
            new Vector3(0.5f, 0.5f, 90f),
            new Vector3(0.5f, 0.5f, 10f));
        var character = AttachToWorld(new Character(new UnitCustomModelParams()), template,
            new Vector3(100f, 200f, 300f));
        character.LocalPingPosition = new WorldSpawnPosition { X = 0.5f, Y = 0.5f, Z = 11f };
        var output = new RecordingMessageOutput();

        new PingPosition().Execute(character, [], output);

        var message = output.Messages.Single();
        await Assert.That(message).Contains("X:0.500 Y:0.500 referenceZ:11.000");
        await Assert.That(message).Contains("terrain=50.000");
        await Assert.That(message).Contains("selectedGround=10.000 source=NavigationNode");
        await Assert.That(message).Contains("legacyHeight=10.000");

        var oldReference = CommandSurfaceResult.Resolve(template, new Vector3(0.5f, 0.5f, 5000f));
        await Assert.That(oldReference.SelectedGround.Height).IsEqualTo(90f);
    }

    [Test]
    public async Task SurfaceCommands_UnavailableGround_ReportsFailureAndPreservesState()
    {
        var template = CreateWorldTemplate(null);
        var queryPosition = new Vector3(0.5f, 0.5f, 73f);
        var surface = CommandSurfaceResult.Resolve(template, queryPosition);
        var markerPosition = new WorldSpawnPosition { X = queryPosition.X, Y = queryPosition.Y, Z = queryPosition.Z };
        var character = AttachToWorld(new Character(new UnitCustomModelParams()), template,
            new Vector3(10f, 20f, 30f));
        character.LocalPingPosition = markerPosition.Clone();
        var originalPosition = character.Transform.World.Position;
        var output = new RecordingMessageOutput();

        var destinationResolved = Teleport.TryBuildPingDestination(surface, out _);
        var markerResolved = TestHeight.TryResolveMarkerPosition(template, markerPosition, out _);
        new Teleport().Execute(character, ["."], output);

        await Assert.That(surface.TerrainHeight).IsNull();
        await Assert.That(surface.SelectedGround.IsResolved).IsFalse();
        await Assert.That(surface.SelectedGround.Failure).IsEqualTo(GroundSurfaceFailure.Unavailable);
        await Assert.That(surface.LegacyHeight).IsNull();
        await Assert.That(surface.FormatHeights()).IsEqualTo(
            "terrain=n/a selectedGround=n/a source=None decision=None failure=Unavailable legacyHeight=n/a");
        await Assert.That(destinationResolved).IsFalse();
        await Assert.That(markerResolved).IsFalse();
        await Assert.That(markerPosition.Z).IsEqualTo(queryPosition.Z);
        await Assert.That(character.DisabledSetPosition).IsFalse();
        await Assert.That(character.Transform.World.Position).IsEqualTo(originalPosition);
        await Assert.That(output.Messages.Single()).Contains("Selected ground is unavailable");
        await Assert.That(output.Messages.Single()).Contains("failure=Unavailable");
    }

    private static WorldTemplate CreateWorldTemplate(ushort? terrainHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "surface_command_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };

        var cell = new WorldCell(0, 0, template);
        if (terrainHeight.HasValue)
            SetPrivateMember(cell, nameof(WorldCell.HeightMap), CreateHeightMap(terrainHeight.Value));
        SetPrivateMember(cell, nameof(WorldCell.Loaded), true);
        template.Cells[0, 0] = cell;
        template.GeoData = new AiGeoDataManager(template);
        return template;
    }

    private static ushort[,] CreateHeightMap(ushort height)
    {
        var heightMap = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        for (var x = 0; x < heightMap.GetLength(0); x++)
        for (var y = 0; y < heightMap.GetLength(1); y++)
            heightMap[x, y] = height;

        return heightMap;
    }

    private static void AddWaypointBai(WorldTemplate template, params Vector3[] positions)
    {
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        for (var i = 0; i < positions.Length; i++)
        {
            var id = i + 1;
            netMission.NodeDescriptorList.TryAdd(id, new NodeDescriptor(netMission)
            {
                Id = id,
                Pos = positions[i],
                NavigationType = BaiNavigationType.WaypointHuman
            });
        }

        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
    }

    private static Character AttachToWorld(Character character, WorldTemplate template, Vector3 position)
    {
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(character, "_parentWorld", world, typeof(GameObject));
        character.Transform.Local.Position = position;
        return character;
    }

    private static void SetPrivateMember(object target, string name, object value, Type declaringType = null)
    {
        var type = declaringType ?? target.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target,
            value);
    }

    private sealed class RecordingMessageOutput : IMessageOutput
    {
        private readonly List<string> _messages = [];

        public IEnumerable<string> Messages => _messages;
        public IEnumerable<string> ErrorMessages => [];

        public void SendMessage(string message)
        {
            _messages.Add(message);
        }

        public void SendMessage(ChatType chatType, string message, Color? color = null)
        {
            _messages.Add(message);
        }

        public void SendMessage(ICharacter target, string message)
        {
            _messages.Add(message);
        }
    }
}
