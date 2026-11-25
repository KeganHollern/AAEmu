using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments("rack-to-hull", true)]
    [Arguments("hull-to-rack", true)]
    [Arguments("stationary", true)]
    [Arguments("repeated", true)]
    [Arguments("fractional", true)]
    [Arguments("reverse-fractional", true)]
    [Arguments("mixed-sign", true)]
    [Arguments("shared-endpoint", true)]
    [Arguments("carried", true)]
    [Arguments("walk", false)]
    [Arguments("small-walk", false)]
    [Arguments("height-change", false)]
    [Arguments("leave", false)]
    [Arguments("other-rack", false)]
    [Arguments("other-ship", false)]
    [Arguments("other-support-first", false)]
    [Arguments("other-skill", false)]
    [Arguments("other-target", false)]
    [Arguments("replaced-pack", false)]
    [Arguments("replaced-at-finish", false)]
    [Arguments("cumulative", false)]
    public async Task LoadedShipRack_OnlyAStationarySupportChangeCompletesThePickup(string action, bool completes)
    {
        var (rack, world, live) = SetUpWorldForCargoPickup();
        using var sus = new SusManager(WorldManager.Instance) { WriteBatch = _ => { } };
        SetInstance(sus);
        var (ship, skill) = SetUpShipRack(rack, world, live, action == "other-target" ? 3054u : 3446u,
            action == "other-skill" ? 15307u : 15309u);
        var pack = PutOriginalPackOnRack(rack);
        if (action == "shared-endpoint")
            rack.Transform.Local.SetPosition(.5f, 11.229750f, 1.881769f);

        // The ordinary player changes contact from the rack top to the deck at the same point.
        var onRack = action switch
        {
            "fractional" or "reverse-fractional" => new Vector3(.25191f, -.13514f, .75039f),
            "mixed-sign" => new Vector3(-.73333f, .00074f, .75357f),
            "shared-endpoint" => new Vector3(-1f / 512, 0, .75f),
            _ => new Vector3(0, 0, .75f)
        };
        var onDeck = rack.Transform.Local.Position + onRack;
        var startsOnDeck = action is "hull-to-rack" or "reverse-fractional";
        BaseUnit initialSupport = startsOnDeck ? ship : rack;
        if (action == "other-support-first")
        {
            var neighbor = new Doodad { ObjId = 201, TemplateId = 3446, ParentWorld = world };
            neighbor.Transform.Parent = ship.Transform;
            neighbor.Transform.Local.SetPosition(rack.Transform.Local.Position);
            live[neighbor.ObjId] = neighbor;
            initialSupport = neighbor;
        }
        _owner.Transform.Parent = initialSupport.Transform;
        MoveAtContact(initialSupport, startsOnDeck ? onDeck : onRack);
        var caster = new SkillCasterUnit(_owner.ObjId);
        var target = new SkillCastDoodadTarget { ObjId = rack.ObjId };
        await Assert.That(skill.Use(_owner, caster, target, new SkillObject(), true, out _)).IsEqualTo(SkillResult.Success);
        var cast = (CastTask)_owner.SkillTask;
        var firstWorld = _owner.Transform.World.Position;
        if (action == "carried")
            ship.Transform.Local.SetPosition(15151, 13572, 100, 0, 0, 1.3f);
        BaseUnit support = startsOnDeck || action is "stationary" or "other-support-first" or "replaced-at-finish" ? rack : ship;
        var position = support == rack ? onRack : onDeck;
        if (action == "walk")
            position.X += .25f;
        if (action == "small-walk")
            position.X += .004f;
        if (action == "height-change")
            position.Z += .002f;
        if (action == "leave")
        {
            support = null;
            position = firstWorld;
        }
        if (action == "other-rack" || action == "other-ship")
        {
            support = action == "other-rack" ? new Doodad { TemplateId = 3446 } : new Slave();
            support.ObjId = 201;
            support.ParentWorld = world;
            support.Transform.Parent = action == "other-rack" ? ship.Transform : null;
            live[support.ObjId] = support;
            position = action == "other-rack" ? onDeck : firstWorld;
        }
        if (action == "replaced-pack")
            rack.ItemId++;
        MoveAtContact(support, position);
        if (action is "repeated" or "cumulative")
        {
            for (var i = 1; i <= 12; i++)
            {
                var returnsToRack = i % 2 == 1;
                var delta = action == "cumulative" ? new Vector3(.0006f * i, 0, 0) : Vector3.Zero;
                MoveAtContact(returnsToRack ? rack : ship, (returnsToRack ? onRack : onDeck) + delta);
            }
        }
        await Assert.That(cast.CastWindow.Active).IsEqualTo(completes || action == "replaced-at-finish");
        if (action == "replaced-at-finish")
            rack.ItemId++;
        if (completes && action != "carried")
            await Assert.That(Vector3.Distance(firstWorld, _owner.Transform.World.Position)).IsLessThan(.005f);
        cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
        cast.Execute();
        cast.Execute();
        await Assert.That(skill.Cancelled).IsEqualTo(!completes);
        await Assert.That(_owner.Inventory.Equipment.Items.Count(i => i.Id == pack.Id)).IsEqualTo(completes ? 1 : 0);
        await Assert.That(_owner.Inventory.Equipment.Items.Contains(pack)).IsEqualTo(completes);
        await Assert.That(_owner.Inventory.SystemContainer.Items.Contains(pack)).IsEqualTo(!completes);
        if (completes)
            await Assert.That(rack.ItemId).IsEqualTo(0ul);
        await Assert.That(pack.Count).IsEqualTo(1);
        await Assert.That(pack.UccId).IsEqualTo(753u);
        await Assert.That(rack.FuncGroupId).IsEqualTo(completes ? 8264u : 8265u);
    }

    [Test]
    [Arguments("hold", true)]
    [Arguments("deck", true)]
    [Arguments("deck-return", true)]
    [Arguments("hull-to-neighbor", true)]
    [Arguments("neighbor-to-target", true)]
    [Arguments("rotated", false)]
    [Arguments("walk", false)]
    [Arguments("unregistered", false)]
    [Arguments("other-deck", false)]
    [Arguments("moved-socket", false)]
    [Arguments("removed-at-finish", false)]
    public async Task LoadedShipRack_CanCollectFromItsInstalledNeighbor(string action, bool completes)
    {
        var (target, world, live) = SetUpWorldForCargoPickup();
        using var sus = new SusManager(WorldManager.Instance) { WriteBatch = _ => { } };
        SetInstance(sus);
        var (ship, skill) = SetUpShipRack(target, world, live, 3446, 15309);
        var pack = PutOriginalPackOnRack(target);
        var neighbor = new Doodad
        {
            ObjId = 201, TemplateId = 3446, Template = target.Template, ParentWorld = world,
            OwnerType = DoodadOwnerType.Slave, OwnerDbId = ship.Id, AttachPoint = AttachPointKind.Box0,
            FuncGroupId = 8264
        };
        neighbor.Transform.Parent = ship.Transform;
        var socket = new Vector3(-.53421f, 11.2297f, 1.87821f);
        neighbor.Transform.Local.SetPosition(socket, Vector3.Zero);
        live[neighbor.ObjId] = neighbor;
        if (action != "unregistered")
            ship.AttachedDoodads.Add(neighbor);

        var localFoot = new Vector3(.0625f, -.076171875f, .0634f);
        BaseUnit startSupport = action == "hull-to-neighbor" ? ship : neighbor;
        _owner.Transform.Parent = startSupport.Transform;
        MoveAtContact(startSupport, startSupport == ship ? socket + localFoot : localFoot);
        var castTarget = new SkillCastDoodadTarget { ObjId = target.ObjId };
        await Assert.That(skill.Use(_owner, new SkillCasterUnit(_owner.ObjId), castTarget,
            new SkillObject(), true, out _)).IsEqualTo(SkillResult.Success);
        var cast = (CastTask)_owner.SkillTask;
        BaseUnit nextSupport = ship;
        var nextFoot = socket + localFoot;
        if (action is "hold" or "hull-to-neighbor")
        {
            nextSupport = neighbor;
            nextFoot = localFoot;
        }
        if (action == "neighbor-to-target")
        {
            nextSupport = target;
            nextFoot -= target.Transform.Local.Position;
        }
        if (action == "rotated")
            neighbor.Transform.Local.SetZRotation(.1f);
        if (action == "other-deck")
        {
            nextSupport = new Slave { ObjId = 207, Template = ship.Template, ParentWorld = world };
            nextSupport.Transform.Local.SetPosition(ship.Transform.Local.Position, ship.Transform.Local.Rotation);
            live[nextSupport.ObjId] = nextSupport;
        }
        if (action == "walk")
            nextFoot.X += .25f;
        if (action == "moved-socket")
            neighbor.Transform.Local.SetPosition(socket + Vector3.UnitY);
        MoveAtContact(nextSupport, nextFoot);
        if (action == "deck-return")
            MoveAtContact(neighbor, localFoot);
        if (action == "removed-at-finish")
            ship.AttachedDoodads.Remove(neighbor);

        cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
        cast.Execute();
        cast.Execute();
        await Assert.That(skill.Cancelled).IsEqualTo(!completes);
        await Assert.That(_owner.Inventory.Equipment.Items.Contains(pack)).IsEqualTo(completes);
        await Assert.That(_owner.Inventory.SystemContainer.Items.Contains(pack)).IsEqualTo(!completes);
        await Assert.That(target.ItemId).IsEqualTo(completes ? 0ul : pack.Id);
        await Assert.That(pack.Count).IsEqualTo(1);
    }

    private (Doodad Rack, WorldInstance World, ConcurrentDictionary<uint, BaseUnit> Units) SetUpWorldForCargoPickup()
    {
        var worlds = new WorldManager(null, null, null, null, null);
        SetInstance(worlds);
        var (rack, _, _) = SetUpSign(true);
        var world = _owner.ParentWorld;
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
        SetField(SkillManager.Instance, "_buffs", new Dictionary<uint, BuffTemplate>());
        MovementValidation.Reset(_owner.ObjId);
        _owner.Transform.InstanceId = world.Id;
        rack.Transform.InstanceId = world.Id;
        var live = (ConcurrentDictionary<uint, BaseUnit>)typeof(WorldInstance)
            .GetField("_baseUnits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        live[_owner.ObjId] = _owner;
        typeof(Character).GetProperty("FinishedLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_owner, true);
        _owner.Connection = new GameConnection(new RankFeedbackSession()) { ActiveChar = _owner };
        return (rack, world, live);
    }

    private (Slave, Skill) SetUpShipRack(Doodad rack, WorldInstance world, ConcurrentDictionary<uint, BaseUnit> live,
        uint rackTemplateId, uint skillId)
    {
        var ship = new Slave { Id = 6, ObjId = 200, ParentWorld = world, Hp = 100,
            OwnerId = _owner.Id, Template = new SlaveTemplate { Id = 75, SlaveKind = SlaveKind.MerchantShip } };
        live[ship.ObjId] = ship;
        ship.Transform.Local.SetPosition(15185.1f, 13537.6f, 99.6748f, 0, 0, -101.4f * MathF.PI / 180f);
        var skill = new Skill(new SkillTemplate { Id = skillId, MaxRange = 20, TargetType = SkillTargetType.Doodad,
            CastingTime = 1000, CustomGcd = 1000 });
        skill.Template.Effects.Add(TargetUse(1513));
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = skill.Template });
        SetField(DoodadManager.Instance, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [8265] = [new() { FuncKey = 9998, SkillId = skillId, GroupId = 8265,
                FuncId = 393, PermId = 5, FuncType = nameof(DoodadFuncRecoverItem), NextPhase = 8264 }]
        });
        SetField(DoodadManager.Instance, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>
        { [nameof(DoodadFuncRecoverItem)] = new() { [393] = new DoodadFuncRecoverItem { Id = 393 } } });
        rack.TemplateId = rackTemplateId;
        rack.Template = new DoodadTemplate { Id = rack.TemplateId, OnceOneMan = true, OnceOneInteraction = true };
        rack.FuncGroupId = 8265;
        rack.AttachPoint = AttachPointKind.Box1;
        rack.OwnerType = DoodadOwnerType.Slave;
        rack.OwnerDbId = ship.Id;
        rack.ParentObjId = ship.ObjId;
        rack.Transform.Parent = ship.Transform;
        rack.Transform.Local.SetPosition(.732762f, 11.229750f, 1.881769f, 0, 0, 0);
        ship.AttachedDoodads.Add(rack);
        var slaves = (ConcurrentDictionary<uint, Slave>)typeof(WorldInstance)
            .GetField("_slaves", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        slaves[ship.ObjId] = ship;
        return (ship, skill);
    }

    private Backpack PutOriginalPackOnRack(Doodad rack)
    {
        var equipment = new EquipmentContainer(_owner.Id, SlotType.Equipment, false, _owner) { Owner = _owner };
        var containers = (Dictionary<ulong, ItemContainer>)typeof(ItemManager)
            .GetField("_allPersistentContainers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ItemManager.Instance)!;
        containers[_owner.Inventory.Equipment.ContainerId] = equipment;
        _owner.Inventory = new Inventory(_owner);
        var packTemplate = new BackpackTemplate { Id = 17683, MaxCount = 1, UseSkillId = 14513 };
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ItemManager.Instance)!;
        templates[packTemplate.Id] = packTemplate;
        var itemData = new ItemGameData();
        SetField(itemData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
        SetInstance(itemData);
        var pack = new Backpack(620, packTemplate, 1) { SlotType = SlotType.System, Slot = 0,
            OwnerId = _owner.Id, UccId = 753, _holdingContainer = _owner.Inventory.SystemContainer };
        pack._holdingContainer.Items.Add(pack);
        _items.Add(pack.Id, pack);
        rack.ItemId = pack.Id;
        rack.ItemTemplateId = pack.TemplateId;
        return pack;
    }

    [Test]
    [Arguments("idle", true)]
    [Arguments("saved-schooner", true)]
    [Arguments("repeat", true)]
    [Arguments("walk", false)]
    [Arguments("stop", false)]
    [Arguments("jump", false)]
    [Arguments("boundary", true)]
    [Arguments("outside", false)]
    [Arguments("scaled-hull", false)]
    [Arguments("out-of-range", false)]
    [Arguments("lost-range-at-finish", false)]
    [Arguments("different-contact", false)]
    [Arguments("turned", false)]
    [Arguments("accumulated", false)]
    [Arguments("height", false)]
    [Arguments("without-solid-contact", false)]
    [Arguments("unreported-at-deadline", false)]
    [Arguments("stale-at-deadline", false)]
    [Arguments("unreported-start", false)]
    [Arguments("on-rack", false)]
    [Arguments("changed-pack", false)]
    public async Task LoadedShipRack_HandlesStandingContactOnTheSameHull(string action, bool completes)
    {
        var (rack, world, live) = SetUpWorldForCargoPickup();
        using var sus = new SusManager(WorldManager.Instance) { WriteBatch = _ => { } };
        SetInstance(sus);
        var (ship, skill) = SetUpShipRack(rack, world, live, 3446, 15309);
        var pack = PutOriginalPackOnRack(rack);
        // The stock Nuian character model (10) uses actor 1, radius 0.55 m.
        _owner.ModelId = 10;
        SetField(ModelManager.Instance, "_modelTypes", new Dictionary<uint, ModelType>
            { [10] = new() { SubType = "ActorModel", SubId = 1 } });
        SetField(ModelManager.Instance, "_models", new Dictionary<string, Dictionary<uint, Model>>
            { ["ActorModel"] = new() { [1] = new ActorModel { Radius = .55f } } });
        if (action == "scaled-hull")
            ship.Scale = 2;
        // Two successive hull-local samples from the standing player on the saved Schooner.
        var start = new Vector3(1.7226562f, 11.097656f, 1.6661f);
        var settled = new Vector3(1.7089844f, 11.095703f, 1.6661f);
        if (action == "saved-schooner")
        {
            start = new Vector3(1.7109375f, 11.099609f, 1.6671f);
            settled = new Vector3(1.7265625f, 11.1015625f, 1.6671f);
        }
        BaseUnit support = action == "on-rack" ? rack : ship;
        _owner.Transform.Parent = support.Transform;
        MoveAtContact(support, start, standingOnSolid: true);
        var target = new SkillCastDoodadTarget { ObjId = rack.ObjId };
        await Assert.That(skill.Use(_owner, new SkillCasterUnit(_owner.ObjId), target,
            new SkillObject(), true, out _)).IsEqualTo(SkillResult.Success);
        var cast = (CastTask)_owner.SkillTask;
        var motion = action switch
        {
            "walk" => MoveTypeFlags.Moving,
            "stop" => MoveTypeFlags.Stopping,
            "jump" => MoveTypeFlags.Jumping,
            _ => MoveTypeFlags.None
        };
        if (action == "height")
            settled.Z += .01f;
        if (action is "boundary" or "outside" or "scaled-hull")
            settled = start - new Vector3((action == "outside" ? 282f : 281f) / 512f, 0, 0);
        if (action == "out-of-range")
            skill.Template.MaxRange = 0;
        MoveAtContact(support, settled, action != "without-solid-contact", motion,
            groundContact: action == "different-contact" ? (byte)0 : (byte)1,
            heading: action == "turned" ? (sbyte)5 : (sbyte)0);
        if (action == "stale-at-deadline")
        {
            MoveAtContact(ship, start, standingOnSolid: true);
            _owner.Transform.Local.SetPosition(settled);
        }
        if (action == "unreported-start")
            _owner.Transform.Local.SetPosition(start);
        if (action == "unreported-at-deadline")
            _owner.Transform.Local.SetPosition(settled + new Vector3(.001953125f, 0, 0));
        if (action == "repeat")
            MoveAtContact(ship, settled, standingOnSolid: true);
        if (action == "accumulated")
        {
            for (var i = 1; i <= 40; i++)
                MoveAtContact(ship, settled + (settled - start) * i, standingOnSolid: true);
        }
        if (action == "changed-pack")
            rack.ItemId++;
        if (action == "lost-range-at-finish")
            skill.Template.MaxRange = 0;
        cast.CastWindow = new CastWindow(DateTime.UtcNow.AddSeconds(-1), true, false, false);
        cast.Execute();
        cast.Execute();
        await Assert.That(skill.Cancelled).IsEqualTo(!completes);
        await Assert.That(_owner.Inventory.Equipment.Items.Contains(pack)).IsEqualTo(completes);
        await Assert.That(_owner.Inventory.SystemContainer.Items.Contains(pack)).IsEqualTo(!completes);
        await Assert.That(pack.Count).IsEqualTo(1);
    }

    private void MoveAtContact(BaseUnit support, Vector3 position, bool standingOnSolid = false,
        MoveTypeFlags motion = MoveTypeFlags.None, byte groundContact = 1, sbyte heading = 0)
    {
        var data = new PacketStream().WriteBc(_owner.ObjId).Write((byte)MoveTypeEnum.Unit);
        new UnitMoveType { X = position.X, Y = position.Y, Z = position.Z, DeltaMovement = new sbyte[3],
            Flags = motion | (support == null ? MoveTypeFlags.None : MoveTypeFlags.StandingOnObject),
            ActorFlags = (byte)((support == null ? MoveTypeActorFlags.None : MoveTypeActorFlags.StandingOnObject) |
                (standingOnSolid ? MoveTypeActorFlags.StandingOnSolid : MoveTypeActorFlags.None)),
            GcFlags = standingOnSolid ? groundContact : (byte)0, RotationZ = heading, GcId = support?.ObjId ?? 0 }.Write(data);
        data.Rollback();
        var step = new CSMoveUnitPacket { Connection = _owner.Connection };
        step.Read(data);
        step.Execute();
    }
}
