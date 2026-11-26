using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    public async Task WorldSupplyBox_ConfirmsAnUnparentedLandingOnlyOnItsIntactSolidTop()
    {
        var world = CreateStockWorld();
        world.Regions = new Region[world.Template.Cells.GetLength(0) * WorldManager.SECTORS_PER_CELL,
            world.Template.Cells.GetLength(1) * WorldManager.SECTORS_PER_CELL];
        var manager = new DoodadManager(null, null, null, null, null);
        typeof(DoodadManager).GetField("_funcsByGroups", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<uint, List<DoodadFunc>>());
        typeof(DoodadManager).GetField("_phaseFuncs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new Dictionary<uint, List<DoodadPhaseFunc>>
            {
                [7071] = [new() { FuncType = "DoodadFuncTimer", FuncId = 1855 }],
                [7078] = [new() { FuncType = "DoodadFuncFinal", FuncId = 1387 }]
            });
        var singleton = typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        var zoneSingleton = typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousZones = zoneSingleton.GetValue(null);
        var zones = new ZoneManager(null, null);
        typeof(ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(zones, new Dictionary<uint, AAEmu.Game.Models.Game.World.Zones.Zone>());
        zoneSingleton.SetValue(null, zones);
        singleton.SetValue(null, manager);
        var housingSingleton = typeof(Singleton<HousingManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousHousing = housingSingleton.GetValue(null);
        var housing = new HousingManager(null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        housingSingleton.SetValue(null, housing);
        try
        {
            Doodad Spawn(uint id, DoodadTemplate crate, WorldSpawnPosition position)
            {
                var doodad = new Doodad
                {
                    ObjId = id, TemplateId = crate.Id, Template = crate, FuncGroupId = crate.FuncGroups[0].Id,
                    ParentWorld = world, OwnerType = DoodadOwnerType.System, IsVisible = true,
                    Spawner = new DoodadSpawner { ParentWorld = world, UnitId = crate.Id, Position = position }
                };
                doodad.Transform.ApplyWorldSpawnPosition(position);
                world.GetRegionByPos(doodad.Transform.World.Position).AddObject(doodad);
                return doodad;
            }

            const string intact = "cgf://objects/env/01_nuia/001_ndeco/ndeco_ruinprop01.cgf";
            const string broken = "prefab://prefabs/interaction_c.xml/toy.barrel_b_bk";
            var template = new DoodadTemplate
            {
                Id = 3091, ModelKindId = 1, Model = intact, FuncGroups =
                [
                    new() { Id = 7070, Model = intact, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start },
                    new() { Id = 7071, Model = intact, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal },
                    new() { Id = 7078, Model = broken, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal }
                ]
            };
            var box = Spawn(42, template, new() { X = 17047.271f, Y = 9552.866f, Z = 122.9357f, Yaw = -116.764f * (MathF.PI / 180) });

            FallUnit Jump(float x = 17047.2f, float z = 123.920f, float y = 9553.3f)
            {
                var actor = new FallUnit { ParentWorld = world, Region = world.GetRegionByPos(new(x, y, z)), Hp = 4026, MaxHp = 4026 };
                actor.Transform.Local.SetPosition(x, y, z + 1.3f);
                actor.ObserveFallMovement(false, 0);
                actor.Transform.Local.SetPosition(x, y, z + 0.975f);
                actor.ObserveFallMovement(false, 0.16);
                actor.Transform.Local.SetPosition(x, y, z);
                actor.ObserveFallMovement(false, 0.32);
                return actor;
            }

            int Land(float x = 17047.2f, float z = 123.920f) => Jump(x, z).Impacts.Count;

            var landing = Jump();
            await Assert.That(landing.Impacts).IsEquivalentTo(new ushort[] { 3120 });
            landing.ObserveFallMovement(true, 0.42);
            await Assert.That(landing.Impacts.Count).IsEqualTo(1);
            await Assert.That(landing.Hp).IsEqualTo(4026);
            box.FuncGroupId = 7071;
            await Assert.That(Land()).IsEqualTo(1);
            await Assert.That(Land(x: 17046)).IsEqualTo(0);
            await Assert.That(Land(z: 123.925f)).IsEqualTo(0);
            template.NoCollision = true;
            await Assert.That(Land()).IsEqualTo(0);
            template.NoCollision = false;
            template.LoadModelFromWorld = true;
            await Assert.That(Land()).IsEqualTo(0);
            template.LoadModelFromWorld = false;
            template.GrowthTime = 3000;
            await Assert.That(Land()).IsEqualTo(0);
            template.GrowthTime = 0;
            box.IsVisible = false;
            await Assert.That(Land()).IsEqualTo(0);
            box.IsVisible = true;
            box.ParentObjId = 20;
            await Assert.That(Land()).IsEqualTo(0);
            box.ParentObjId = 0;
            box.Spawner.FollowNpc = 12;
            await Assert.That(Land()).IsEqualTo(0);
            box.Spawner.FollowNpc = 0;
            box.FuncGroupId = 7078;
            await Assert.That(Land()).IsEqualTo(0);
            box.FuncGroupId = 7070;
            box.Transform.Local.SetRotation(0, 0.15f, -116.764f * (MathF.PI / 180));
            await Assert.That(Land()).IsEqualTo(0);
            box.Transform.Local.SetRotation(0, 0, -116.764f * (MathF.PI / 180));
            await Assert.That(Land()).IsEqualTo(1);
            world.GetRegionByPos(box.Transform.World.Position).RemoveObject(box);
            await Assert.That(Land()).IsEqualTo(0);
            await Assert.That(Land(z: 122.93604f)).IsEqualTo(1);

            // Another authored crate supplies its model with a full game/ path.
            const string prefixed = "cgf://game/Objects/env/02_harihara/001_hdeco/hdeco_box_b.cgf";
            var outdoor = new DoodadTemplate
            {
                Id = 5534, ModelKindId = 1, Model = prefixed,
                FuncGroups = [new() { Id = 14187, Model = prefixed, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }]
            };
            Spawn(43, outdoor, new() { X = 20058.553f, Y = 7592.282f, Z = 179.9769f, Yaw = -147.8655f * (MathF.PI / 180) });
            var outdoorLanding = Jump(20058.553f, 180.48383f, 7592.282f);
            await Assert.That(outdoorLanding.Impacts.Count).IsEqualTo(1);
            await Assert.That(world.Template.GeoData.HasStaticDoodadFallSupport(outdoorLanding, outdoorLanding.Transform.World.Position)).IsTrue();
        }
        finally
        {
            housingSingleton.SetValue(null, previousHousing);
            singleton.SetValue(null, previous);
            zoneSingleton.SetValue(null, previousZones);
        }
    }
}
