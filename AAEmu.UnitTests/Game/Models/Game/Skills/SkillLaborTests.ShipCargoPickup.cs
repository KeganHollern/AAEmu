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
        var worlds = new WorldManager(null, null, null, null, null);
        SetInstance(worlds);
        var (rack, _, _) = SetUpSign(true);
        var world = _owner.ParentWorld;
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
        using var sus = new SusManager(worlds) { WriteBatch = _ => { } };
        SetInstance(sus);
        SetField(SkillManager.Instance, "_buffs", new Dictionary<uint, BuffTemplate>());
        MovementValidation.Reset(_owner.ObjId);
        _owner.Transform.InstanceId = world.Id;
        rack.Transform.InstanceId = world.Id;
        var live = (ConcurrentDictionary<uint, BaseUnit>)typeof(WorldInstance)
            .GetField("_baseUnits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        live[_owner.ObjId] = _owner;
        typeof(Character).GetProperty("FinishedLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_owner, true);
        _owner.Connection = new GameConnection(new RankFeedbackSession()) { ActiveChar = _owner };
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

    private void MoveAtContact(BaseUnit support, Vector3 position)
    {
        var data = new PacketStream().WriteBc(_owner.ObjId).Write((byte)MoveTypeEnum.Unit);
        new UnitMoveType { X = position.X, Y = position.Y, Z = position.Z, DeltaMovement = new sbyte[3],
            Flags = support == null ? MoveTypeFlags.None : MoveTypeFlags.StandingOnObject,
            ActorFlags = support == null ? (byte)0 : (byte)MoveTypeActorFlags.StandingOnObject, GcId = support?.ObjId ?? 0 }.Write(data);
        data.Rollback();
        var step = new CSMoveUnitPacket { Connection = _owner.Connection };
        step.Read(data);
        step.Execute();
    }
}
