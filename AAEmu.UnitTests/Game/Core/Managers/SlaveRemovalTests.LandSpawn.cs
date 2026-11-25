using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed partial class SlaveRemovalTests
{
    [Test]
    [Arguments("outdoor", 30f, 30.3f, 30f)]
    [Arguments("layer", 30f, 80f, 81f)]
    [Arguments("layer", 0f, -4f, -3f)]
    [Arguments("zero", 0f, 0.4f, 0f)]
    [Arguments("missing", 0f, 63f, 63f)]
    [Arguments("preset", 30f, 30.07f, 30f)]
    [Arguments("preset-missing", 0f, 63f, 63f)]
    [Arguments("explicit", 30f, 80f, 80f)]
    public async Task LandVehicleSpawn_UsesSelectedFloorOrRequestedPose(string route, float terrainZ, float requestedZ, float expectedZ)
    {
        const uint cart = 73;
        var position = new Vector3(100, 105, requestedZ);
        SetCartGround(route, terrainZ, position);
        SetVehicleSpawnServices(new SlaveTemplate { Id = cart, SlaveKind = SlaveKind.Machine, Name = "Cotton Wagon" });
        // This summon uses an in-memory character and no saved vehicle.
        _owner.Id = 0;
        _owner.Transform.Local.SetPosition(100, 100, requestedZ);

        Slave spawned;
        if (route.StartsWith("preset", StringComparison.Ordinal))
        {
            var spawner = new SlaveSpawner { World = _world, UnitId = cart, Position = new WorldSpawnPosition
                { X = position.X, Y = position.Y, Z = position.Z, Yaw = 0.75f } };
            spawned = spawner.Spawn(0);
            await Assert.That(spawned.Transform.World.Rotation.Z).IsEqualTo(0.75f);
        }
        else if (route == "explicit")
        {
            using var accepted = new Transform(null);
            accepted.Local.SetPosition(position);
            accepted.Local.SetRotation(0.1f, 0.2f, 0.75f);
            spawned = _world.SlaveManager.Create(cart, true, accepted);
            await Assert.That(spawned.Transform.World.Rotation).IsEqualTo(accepted.World.Rotation);
        }
        else
        {
            spawned = _world.SlaveManager.Create(_owner, null, cart);
        }

        await Assert.That(spawned.Transform.World.Position).IsEqualTo(new Vector3(position.X, position.Y, expectedZ));
    }

    private void SetCartGround(string route, float terrainZ, Vector3 position)
    {
        var template = _world.Template;
        template.HeightMaxCoefficient = 1;
        template.Cells = new WorldCell[1, 1];
        template.GeoData = new AiGeoDataManager(template);
        if (route is "missing" or "preset-missing")
            return;

        var cell = template.Cells[0, 0] = new WorldCell(0, 0, template);
        var map = new ushort[WorldManager.CELL_HMAP_RESOLUTION, WorldManager.CELL_HMAP_RESOLUTION];
        // These four samples surround the forward spawn position at (100, 105).
        map[50, 52] = map[51, 52] = map[50, 53] = map[51, 53] = (ushort)terrainZ;
        typeof(WorldCell).GetProperty(nameof(WorldCell.HeightMap), BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cell, map);
        typeof(WorldCell).GetProperty(nameof(WorldCell.Loaded))!.SetValue(cell, true);
        var bai = new BaseBaiLoader(template);
        var mission = new NetMissionReader(Stream.Null, 0);
        mission.NodeDescriptorList.TryAdd(1, new NodeDescriptor(mission)
        {
            Id = 1, Pos = position + Vector3.UnitZ,
            NavigationType = route == "layer" ? BaiNavigationType.WaypointHuman : BaiNavigationType.Triangular
        });
        bai.NetMissionReaders.Add(mission);
        template.ZoneBaiLoader.Add(0, bai);
    }

    private void SetVehicleSpawnServices(params SlaveTemplate[] templates)
    {
        foreach (var ids in new IdManager[] { new TlIdManager(), new ObjectIdManager() })
        {
            var field = ids.GetType().GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
            _singletons.Add((field, field.GetValue(null)));
            ids.Initialize();
            field.SetValue(null, ids);
        }
        SetField(SlaveGameData.Instance, "_slaveTemplates", templates.ToDictionary(template => template.Id));
        var formulas = new FormulaManager();
        var constant = new UnitFormula();
        typeof(Formula).GetProperty("Expression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(constant, (Func<Dictionary<string, double>, double>)(_ => 100));
        SetField(formulas, "_unitFormulas", new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>>
            { [FormulaOwnerType.Slave] = Enum.GetValues<UnitFormulaKind>().ToDictionary(kind => kind, _ => constant) });
        SetField(formulas, "_unitVariables", new Dictionary<uint, Dictionary<UnitFormulaVariableType, Dictionary<uint, UnitFormulaVariable>>>());
        Install(formulas);
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        Install(skills);
        Install(new ItemManager(null, null, null, null, null, null));
        var factions = new FactionManager(null);
        SetField(factions, "_systemFactions", new Dictionary<FactionsEnum, SystemFaction>());
        Install(factions);
    }
}
