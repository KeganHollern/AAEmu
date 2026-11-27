using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Housing;
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
        await WithStaticDoodadWorld(async (world, _) =>
        {
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
            var box = Spawn(world, 42, template, new() { X = 17047.271f, Y = 9552.866f, Z = 122.9357f, Yaw = -116.764f * (MathF.PI / 180) });

            int Land(float x = 17047.2f, float z = 123.920f) => Jump(world, x, z).Impacts.Count;

            var landing = Jump(world);
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
            Spawn(world, 43, outdoor, new() { X = 20058.553f, Y = 7592.282f, Z = 179.9769f, Yaw = -147.8655f * (MathF.PI / 180) });
            var outdoorLanding = Jump(world, 20058.553f, 180.48383f, 7592.282f);
            await Assert.That(outdoorLanding.Impacts.Count).IsEqualTo(1);
            await Assert.That(world.Template.GeoData.HasStaticDoodadFallSupport(outdoorLanding, outdoorLanding.Transform.World.Position)).IsTrue();
        });
    }

    private async Task WithStaticDoodadWorld(Func<WorldInstance, HousingManager, Task> check)
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
                [3333] = [new() { FuncType = "DoodadFuncClout", FuncId = 226 }],
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
            await check(world, housing);
        }
        finally
        {
            housingSingleton.SetValue(null, previousHousing);
            singleton.SetValue(null, previous);
            zoneSingleton.SetValue(null, previousZones);
        }
    }

    private static Doodad Spawn(WorldInstance world, uint id, DoodadTemplate crate, WorldSpawnPosition position)
    {
        var doodad = new Doodad
        {
            ObjId = id, TemplateId = crate.Id, Template = crate, FuncGroupId = crate.FuncGroups[0].Id,
            ParentWorld = world, OwnerType = DoodadOwnerType.System, IsVisible = true,
            Spawner = new DoodadSpawner { ParentWorld = world, UnitId = crate.Id, Position = position }
        };
        doodad.Transform.ApplyWorldSpawnPosition(position);
        doodad.Region = world.GetRegionByPos(doodad.Transform.World.Position);
        doodad.Region.AddObject(doodad);
        return doodad;
    }

    private static FallUnit Jump(WorldInstance world, float x = 17047.2f, float z = 123.920f, float y = 9553.3f)
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

    [Test]
    public async Task OutdoorFurnace_PlayerLandsOnTheSlopedStoneBeforeTheLandingNotice()
    {
        await WithStaticDoodadWorld(async (world, _) =>
        {
            world.Template.GetCell(13, 15).VerifyCellLoaded();
            var template = new DoodadTemplate
            {
                Id = 1749, ModelKindId = 1, Model = "cgf://objects/env/01_nuia/001_ndeco/making/ndeco_making_furnace01.cgf",
                FuncGroups =
                [
                    new() { Id = 3332, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start },
                    new() { Id = 3333, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal }
                ]
            };
            var furnace = Spawn(world, 70, template, new()
            {
                X = 13674.676f, Y = 15590.036f, Z = 236.1017f, Yaw = 29.8806f * MathF.PI / 180
            });
            var player = new InclinePlayer { ParentWorld = world, Region = furnace.Region, Hp = 9516 };
            var ground = world.Template.GeoData;
            // The exposed default collision triangle is 41.063 degrees above horizontal.
            var foot = Encoded(new Vector3(13674.762f, 15590.14f, 237.43259f));
            await Assert.That(ground.HasStaticDoodadFallSupport(player, foot + Vector3.UnitZ * 0.04f)).IsFalse();
            await Assert.That(ground.HasStaticDoodadFallSupport(player, foot - Vector3.UnitZ * 0.04f)).IsFalse();
            await Assert.That(ground.HasStaticDoodadFallSupport(new FallUnit { ParentWorld = world, Region = furnace.Region }, foot)).IsFalse();

            player.Transform.Local.SetPosition(foot + Vector3.UnitZ * 1.3f);
            player.ObserveFallMovement(false, 0);
            player.Transform.Local.SetPosition(foot + Vector3.UnitZ * 0.975f);
            player.ObserveFallMovement(false, 0.16);
            await Assert.That(player.Impacts).IsEmpty();
            player.Transform.Local.SetPosition(foot);
            player.ObserveFallMovement(false, 0.32);
            await Assert.That(player.Impacts).IsEquivalentTo(new ushort[] { 3120 });
            player.ObserveFallMovement(true, 0.42);
            await Assert.That(player.Impacts.Count).IsEqualTo(1);

            // Across this edge the next physical face is 0.38 m lower and too steep to stand on.
            await Assert.That(ground.HasStaticDoodadFallSupport(player, Encoded(new(13674.846f, 15590.342f, 237.28285f)))).IsTrue();
            await Assert.That(ground.HasStaticDoodadFallSupport(player, Encoded(new(13674.857f, 15590.371f, 237.26f)))).IsFalse();
            await Assert.That(ground.HasStaticDoodadFallSupport(player, Encoded(new(13674.857f, 15590.371f, 236.89719f)))).IsFalse();
            // The lower lip is 23.81 degrees. The southeast face uses the stock bounds index beyond 2 m.
            await Assert.That(ground.HasStaticDoodadFallSupport(player, Encoded(new(13674.499f, 15590.774f, 237.17398f)))).IsTrue();
            await Assert.That(ground.HasStaticDoodadFallSupport(player, Encoded(new(13675.551f, 15588.234f, 237.29785f)))).IsTrue();
            furnace.FuncGroupId = 3333;
            await Assert.That(ground.HasStaticDoodadFallSupport(player, foot)).IsFalse();
            furnace.FuncGroupId = 3332;
            furnace.ParentObjId = 15;
            await Assert.That(ground.HasStaticDoodadFallSupport(player, foot)).IsFalse();
        });

        static Vector3 Encoded(Vector3 point)
        {
            var position = Helpers.ConvertPosition(Helpers.ConvertPosition(point.X, point.Y, point.Z));
            return new Vector3(position.x, position.y, position.z);
        }
    }

    [Test]
    public async Task OutdoorFurnace_ConfirmsFirstFarLandingAndKeepsItsEdgeOpen()
    {
        await WithStaticDoodadWorld(async (world, housing) =>
        {
            const string path = "cgf://objects/Env/01_nuia/001_ndeco/making/ndeco_making_furnace01.cgf";
            var template = new DoodadTemplate
            {
                Id = 1749, ModelKindId = 1, Model = path, FuncGroups =
                [
                    new() { Id = 3332, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start },
                    new() { Id = 3333, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal }
                ]
            };
            var furnace = Spawn(world, 70, template, new()
            {
                X = 13674.676f, Y = 15590.036f, Z = 236.1017f, Yaw = 29.8806f * (MathF.PI / 180)
            });
            // This is the first approach. The foot is 2.253 m from the origin in XY.
            int Land(float y) => Jump(world, 13673.426f, 236.19403f, y).Impacts.Count;
            await Assert.That(Land(15591.911f)).IsEqualTo(1);
            await Assert.That(Land(15591.967f)).IsEqualTo(0);
            await Assert.That(Land(15591.411f)).IsEqualTo(1);

            var rollback = furnace.CaptureLaborState();
            furnace.FuncGroupId = 3333;
            await Assert.That(Land(15591.911f)).IsEqualTo(0);
            rollback();
            await Assert.That(Land(15591.911f)).IsEqualTo(1);

            // A second placement straddles a 64 m visibility-region boundary.
            var second = Spawn(world, 71, template, new() { X = 13696.25f, Y = 15591, Z = 450 });
            second.SetScale(2);
            // Convert the measured contact in the first placement back to the model frame.
            var delta = new Vector3(-1.25f, 1.875f, 0.09233f);
            var local = Vector3.Transform(delta, Matrix4x4.CreateRotationZ(-29.8806f * MathF.PI / 180));
            var foot = second.Transform.World.Position + local * 2;
            await Assert.That(world.GetRegionByPos(foot) == second.Region).IsFalse();
            int LandAtSecond() => Jump(world, foot.X, foot.Z, foot.Y).Impacts.Count;
            await Assert.That(LandAtSecond()).IsEqualTo(1);
            var restore = second.CaptureLaborState();
            second.SetScale(0.5f);
            await Assert.That(LandAtSecond()).IsEqualTo(0);
            restore();
            await Assert.That(LandAtSecond()).IsEqualTo(1);
            second.Region.RemoveObject(second);
            await Assert.That(LandAtSecond()).IsEqualTo(0);

            // The crate is narrower than 2 m in XY, but its outer top crosses the 3D sphere.
            var stack = new DoodadTemplate
            {
                Id = 3010, ModelKindId = 1, Model = "cgf://objects/env/01_nuia/001_ndeco/ndeco_freebox_a.cgf",
                FuncGroups = [new() { Id = 6838, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start }]
            };
            Spawn(world, 74, stack, new() { X = 20943.908f, Y = 18835.242f, Z = 117.0114f });
            await Assert.That(Jump(world, 20944.758f, 118.97698f, 18836.092f).Impacts.Count).IsEqualTo(1);
            await Assert.That(Jump(world, 20944.758f, 118.99698f, 18836.092f).Impacts.Count).IsEqualTo(0);

            // An independent geometry source can replace or omit the named CGF.
            foreach (var missing in new[] { false, true })
            {
                Stream Read(string name)
                {
                    if (!name.EndsWith("ndeco_making_furnace01.cgf", StringComparison.OrdinalIgnoreCase))
                        return _stockSource.FileExists(name) ? _stockSource.GetFileStream(name) : null;
                    if (missing)
                        return null;
                    // Same physical faces, different file hash. The ordinary close contact still works.
                    using var stock = _stockSource.GetFileStream(name);
                    var changed = new MemoryStream();
                    stock.CopyTo(changed);
                    changed.WriteByte(0);
                    changed.Position = 0;
                    return changed;
                }
                housing.GeometryAssets = new HousingGeometryAssets(Read, (_, _) => []);
                var copy = Spawn(world, missing ? 73u : 72u, template,
                    new() { X = 13674.676f, Y = 15590.036f, Z = 466, Yaw = 29.8806f * MathF.PI / 180 });
                await Assert.That(Jump(world, 13673.426f, 466.09233f, 15591.911f).Impacts.Count).IsEqualTo(0);
                if (!missing)
                    await Assert.That(Jump(world, 13673.426f, 466.09233f, 15591.411f).Impacts.Count).IsEqualTo(1);
                copy.Region.RemoveObject(copy);
            }
        });
    }
}
