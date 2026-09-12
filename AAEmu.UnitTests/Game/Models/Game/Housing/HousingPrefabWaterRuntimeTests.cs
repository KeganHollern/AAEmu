using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingPrefabWaterRuntimeTests
{
    [Test]
    [NotInParallel]
    public async Task GetPrefabWater_UsesCurrentModelsAndWorldWithFourNewestRegistrations()
    {
        static string Uri(int surface) => $"prefab://prefabs/water.xml/water{surface}";
        var xml = Encoding.UTF8.GetBytes("<PrefabsLibrary>" + string.Concat(new[] { 12, 13, 14, 30, 40, 60, 70, 80 }
            .Select(surface => $"""
                <Prefab Name="water{surface}"><Objects>
                  <Object Type="WaterVolume" Pos="0,0,{surface}" VolumeDepth="100"><Points>
                    <Point Pos="0,0,0"/><Point Pos="4,0,0"/><Point Pos="4,4,0"/><Point Pos="0,4,0"/>
                  </Points></Object>
                </Objects></Prefab>
                """)) + "</PrefabsLibrary>");
        var assets = new HousingGeometryAssets(path => path == "game/prefabs/water.xml" ? new MemoryStream(xml) : null,
            (model, _) => [Uri((int)model)]);
        var template = new WorldTemplate { Id = 1 };
        var world = new WorldInstance(template, 0, true, 1);
        var otherWorld = new WorldInstance(template, 1, true, 2);
        using var worldState = new WorldState(world, otherWorld);
        var time = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        var phase = typeof(Doodad).GetField("_funcGroupId", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Doodad AddDoodad(uint id, int surface, DateTime start)
        {
            var doodad = new Doodad
            {
                ObjId = id, Template = new DoodadTemplate { Model = Uri(surface) },
                ParentWorld = world, PhaseTime = start
            };
            doodad.Transform.Local.SetPosition(100, 200, 300);
            world.AddObject(doodad);
            return doodad;
        }

        // Registration order deliberately differs from both object ID and phase time.
        var oldest = AddDoodad(90, 40, time.AddSeconds(-1));
        AddDoodad(30, 13, time);
        var active = AddDoodad(10, 60, time);
        active.Template.FuncGroups.Add(new DoodadFuncGroups { Id = 7, Model = Uri(30) });
        phase.SetValue(active, 7u);
        AddDoodad(20, 12, time);

        var houseTemplate = new HousingTemplate { MainModelId = 70 };
        houseTemplate.BuildSteps.Add(0, new HousingBuildStep { ModelId = 14 });
        var house = new House { ObjId = 5, Template = houseTemplate, ParentWorld = world, PlaceDate = time };
        house.SetInitialConstructionStep();
        house.Transform.Local.SetPosition(100, 200, 300);
        var foreignHouse = new House
        {
            ObjId = 999, Template = new HousingTemplate { MainModelId = 80 },
            ParentWorld = otherWorld, PlaceDate = time.AddSeconds(1)
        };
        foreignHouse.SetInitialConstructionStep();
        foreignHouse.Transform.Local.SetPosition(100, 200, 300);
        House[] houses = [foreignHouse, house];
        var geometry = new HousingWaterGeometry();
        var point = new Vector3(102, 202, 309);

        // The current phase contributes 330, the construction step contributes 314,
        // and the four newest local volumes exclude the older surface at 340.
        await Assert.That(geometry.GetWaterLevel(point, 300, assets.GetPrefabWater(world, houses))).IsEqualTo(330f);
        phase.SetValue(active, 0u);
        await Assert.That(geometry.GetWaterLevel(point, 300, assets.GetPrefabWater(world, houses))).IsEqualTo(360f);
        phase.SetValue(active, 7u);

        // Equal phase times use object ID across doodads and houses. ID 90 now enters
        // the four-result set, and house ID 5 leaves it even after its surface rises to 364.
        // The other world's newer surface at 380 also stays excluded.
        oldest.PhaseTime = time;
        house.Transform.Local.SetPosition(100, 200, 350);
        await Assert.That(geometry.GetWaterLevel(point, 300, assets.GetPrefabWater(world, houses))).IsEqualTo(340f);
    }

    private sealed class WorldState : IDisposable
    {
        private readonly FieldInfo _singleton = typeof(Singleton<WorldManager>)
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object _previous;

        public WorldState(params WorldInstance[] worlds)
        {
            _previous = _singleton.GetValue(null);
            var manager = new WorldManager(null, null, null, null, null);
            var instances = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            foreach (var world in worlds)
                instances.TryAdd(world.Id, world);
            _singleton.SetValue(null, manager);
        }

        public void Dispose() => _singleton.SetValue(null, _previous);
    }
}
