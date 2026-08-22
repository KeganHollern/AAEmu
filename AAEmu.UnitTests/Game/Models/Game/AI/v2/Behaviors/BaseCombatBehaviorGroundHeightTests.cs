using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Behaviors;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.AI.v2.Behaviors;

public class BaseCombatBehaviorGroundHeightTests
{
    [Test]
    public async Task CheckPipeName_NamedGroundPhase_UsesTerrainForFlyingNpc()
    {
        var template = CreateWorldTemplate(50);
        AddNetMissionBai(template, new Vector3(0.5f, 0.5f, 90f), BaiNavigationType.Triangular);
        var npc = AttachToWorld(new Npc { CanFly = true }, template, new Vector3(0.5f, 0.5f, 53f));
        var behavior = CreateBehavior(npc);

        var legacyFound = template.GeoData.TryGetHeight(npc.Transform.World.Position, out var legacyHeight);
        behavior.CheckPipeName("phase_dragon_ground", 0);

        await Assert.That(legacyFound).IsTrue();
        await Assert.That(legacyHeight).IsEqualTo(90f);
        await Assert.That(npc.CanFly).IsTrue();
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(50f);
    }

    [Test]
    public async Task CheckPipeName_NumericGroundPhase_UnavailableSurface_PreservesHeight()
    {
        const float initialHeight = 73f;
        var template = CreateWorldTemplate(null);
        var npc = AttachToWorld(new Npc { CanFly = true }, template, new Vector3(0.5f, 0.5f, initialHeight));
        var behavior = CreateBehavior(npc);

        behavior.CheckPipeName(string.Empty, 1);

        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(initialHeight);
    }

    [Test]
    public async Task CheckPipeName_GroundPhase_SeaLevelTerrain_LandsWithoutTolerance()
    {
        var template = CreateWorldTemplate(0);
        var npc = AttachToWorld(new Npc { CanFly = true }, template, new Vector3(0.5f, 0.5f, 200f));
        var behavior = CreateBehavior(npc);

        behavior.CheckPipeName("phase_dragon_ground", 0);

        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(0f);
    }

    [Test]
    public async Task CheckPipeName_HoverPhase_AppliesHeightOffsetOnlyOnce()
    {
        const float initialHeight = 100f;
        var template = CreateWorldTemplate(null);
        var npc = AttachToWorld(new Npc { CanFly = true }, template, new Vector3(0.5f, 0.5f, initialHeight));
        var behavior = CreateBehavior(npc);

        behavior.CheckPipeName("phase_dragon_fly_hovering", 2);
        behavior.CheckPipeName("phase_dragon_fly_hovering", 2);

        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(initialHeight + 15f);
    }

    private static TestBaseCombatBehavior CreateBehavior(Npc npc)
    {
        var ai = new DummyAiCharacter { Owner = npc };
        npc.Ai = ai;
        return new TestBaseCombatBehavior { Ai = ai };
    }

    private static WorldTemplate CreateWorldTemplate(ushort? groundHeight)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "base_combat_behavior_ground_height_test",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = 1d,
            Cells = new WorldCell[1, 1]
        };

        var cell = new WorldCell(0, 0, template);
        if (groundHeight.HasValue)
            SetPrivateMember(cell, nameof(WorldCell.HeightMap), CreateHeightMap(groundHeight.Value));
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

    private static void AddNetMissionBai(WorldTemplate template, Vector3 position,
        BaiNavigationType navigationType)
    {
        var bai = new BaseBaiLoader(template);
        var netMission = new NetMissionReader(Stream.Null, 1);
        netMission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(netMission)
        {
            Id = 1,
            Pos = position,
            NavigationType = navigationType
        });
        bai.NetMissionReaders.Add(netMission);
        template.ZoneBaiLoader.Add(1, bai);
    }

    private static Npc AttachToWorld(Npc npc, WorldTemplate template, Vector3 position)
    {
        var world = new WorldInstance(template, 1, true, 1);
        SetPrivateMember(npc, "_parentWorld", world, typeof(GameObject));
        npc.Transform.Local.SetPosition(position.X, position.Y, position.Z);
        return npc;
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

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class TestBaseCombatBehavior : BaseCombatBehavior
    {
        public void CheckPipeName(string pipeName, uint phaseType)
        {
            _pipeName = pipeName;
            _phaseType = phaseType;
            base.CheckPipeName();
        }

        public override void Enter()
        {
        }

        public override void Tick(TimeSpan delta)
        {
        }

        public override void Exit()
        {
        }
    }
}
