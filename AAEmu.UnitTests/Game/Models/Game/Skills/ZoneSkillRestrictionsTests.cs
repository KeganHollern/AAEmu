using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Mate;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.GameData;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class ZoneSkillRestrictionsTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private ZoneManager _zones;
    private ProbeUnit _caster;

    [Before(Test)]
    public void SetUp()
    {
        _zones = new ZoneManager(null, null);
        using var connection = CreateData();
        _zones.LoadBannedTags(connection);
        SetField(_zones, "_zones", new Dictionary<uint, Zone>
        {
            [1000] = new() { ZoneKey = 1000, GroupId = 45 },
            [2000] = new() { ZoneKey = 2000, GroupId = 49 },
            [3000] = new() { ZoneKey = 3000, GroupId = 1 }
        });
        SetInstance(_zones);
        var tags = new TagsGameData();
        tags.Load(connection);
        SetInstance(tags);
        var worldManager = new WorldManager(null, null, null, null, null);
        SetInstance(worldManager);
        SetInstance(new UnitRequirementsGameData());
        SetInstance(new SkillRequirementsGameData());
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillTags", new Dictionary<uint, List<uint>> { [12373] = [296] });
        SetInstance(skills);

        var template = new WorldTemplate
        {
            Id = 10,
            CellX = 1,
            CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        };
        template.ZoneKeyByRegions[0, 0] = 1000;
        template.ZoneKeyByRegions[1, 0] = 2000;
        template.ZoneKeyByRegions[2, 0] = 3000;
        var world = new WorldInstance(template, 0, true, 0);
        _caster = new ProbeUnit { ObjId = 7, Mp = 100 };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_caster, world);
        world.AddObject(_caster);
        _caster.Transform.Local.SetPosition(10, 10, 100);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, instance) in _previousInstances)
            field.SetValue(null, instance);
    }

    [Test]
    [Arguments(45u, 12373u, 0u, 21u)]
    [Arguments(45u, 0u, 820u, 2u)]
    [Arguments(45u, 0u, 150u, 3u)]
    [Arguments(45u, 0u, 1449u, 13u)]
    [Arguments(45u, 0u, 14677u, 16u)]
    [Arguments(45u, 21571u, 0u, 185u)]
    [Arguments(49u, 0u, 150u, 160u)]
    [Arguments(49u, 0u, 1449u, 20u)]
    [Arguments(49u, 21571u, 0u, 186u)]
    [Arguments(49u, 12373u, 14677u, 0u)]
    [Arguments(45u, 2u, 0u, 0u)]
    [Arguments(1u, 12373u, 14677u, 0u)]
    [Arguments(999u, 21571u, 150u, 0u)]
    public async Task AuthoredTags_BlockOnlyTheirZoneGroups(uint group, uint skill, uint item, uint expected)
    {
        await Assert.That(_zones.GetBannedAction(group, skill, item)?.Id ?? 0).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0u, false)]
    [Arguments(1u, false)]
    [Arguments(2u, false)]
    [Arguments(4u, false)]
    [Arguments(8u, true)]
    [Arguments(16u, true)]
    [Arguments(24u, true)]
    [Arguments(32u, false)]
    public async Task SiegeMask_UsesNativeBitOverlapAndNoDominionIsZero(uint mask, bool expected)
    {
        await Assert.That(_zones.GetBannedTag(63, new HashSet<uint> { 1338 }, mask) != null).IsEqualTo(expected);
        await Assert.That(_zones.IsTagBanned(63, 1338)).IsFalse();
        await Assert.That(_zones.GetBannedTag(45, new HashSet<uint> { 296 }, mask) != null).IsTrue();
    }

    [Test]
    public async Task CurrentCoordinates_OverrideStaleTransformZoneAndFollowZoneExit()
    {
        _caster.Transform.ZoneId = 3000;
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 12373)?.Id).IsEqualTo(21u);
        _caster.Transform.Local.SetPosition(130, 10, 100);
        _caster.Transform.ZoneId = 1000;
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 12373)).IsNull();
        _caster.Transform.Local.SetPosition(10, 10, 100);
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 12373)?.Id).IsEqualTo(21u);
    }

    [Test]
    public async Task DestinationCheck_UsesDestinationCoordinatesAndStillChecksCasterZone()
    {
        _caster.Transform.Local.SetPosition(130, 10, 100);
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 21571, destination: new Vector3(70, 10, 100))?.Id).IsEqualTo(186u);
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 0, 1449, new Vector3(10, 10, 100))?.Id).IsEqualTo(13u);
        _caster.Transform.Local.SetPosition(10, 10, 100);
        await Assert.That(ZoneSkillRestrictions.GetBan(_caster, 21571, destination: new Vector3(130, 10, 100))?.Id).IsEqualTo(185u);
    }

    [Test]
    public async Task SkillUse_ZoneFailurePrecedesGcdManaAndBuffRemoval()
    {
        var skill = NewSkill();
        var originalTime = _caster.SkillLastUsed;
        var result = skill.Use(_caster, new SkillCasterUnit(7), new SkillCastUnitTarget(7), null, false, out var detail);
        await Assert.That(result).IsEqualTo(SkillResult.ZoneBanned);
        await Assert.That(detail).IsEqualTo(21u);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_caster.Mp).IsEqualTo(100);
        await Assert.That(_caster.SkillLastUsed).IsEqualTo(originalTime);
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task SkillUse_BuffFailurePrecedesZoneFailureAndKeepsRequirementId()
    {
        using var connection = SkillRequirementsGameDataTests.CreateConnection();
        SkillRequirementsGameDataTests.Execute(connection, "INSERT INTO skill_reqs VALUES(13, 'f', NULL, 306, 't'); INSERT INTO skill_req_skills VALUES(13, 12373);");
        SetInstance(SkillRequirementsGameDataTests.Load(connection));
        var buffs = Mock.Of<IBuffs>();
        buffs.CheckBuffTag(306).Returns(true);
        _caster.Buffs = buffs.Object;
        var skill = NewSkill();
        var result = skill.Use(_caster, new SkillCasterUnit(7), new SkillCastUnitTarget(7), null, false, out var detail);
        await Assert.That(result).IsEqualTo(SkillResult.SkillReqFail);
        await Assert.That(detail).IsEqualTo(13u);
        await Assert.That(_caster.Mp).IsEqualTo(100);
        await Assert.That(skill.Cancelled).IsTrue();
    }

    [Test]
    public async Task CastCompletion_RechecksZoneBeforeManaAndEndsCancelledCast()
    {
        var skill = NewSkill();
        skill.Cast(_caster, new SkillCasterUnit(7), _caster, new SkillCastUnitTarget(7), null);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_caster.Mp).IsEqualTo(100);
        await Assert.That(_caster.EndedSkills).IsEqualTo(1);
    }

    [Test]
    public async Task DelayedEffects_RecheckZoneBeforeEffectSelection()
    {
        var skill = NewSkill();
        skill.ApplyEffects(_caster, new SkillCasterUnit(7), _caster, new SkillCastUnitTarget(7), null);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_caster.Mp).IsEqualTo(100);
    }

    [Test]
    public async Task PlotEffects_UseOriginalCasterBeforeSourceSubstitution()
    {
        var skill = NewSkill();
        var state = new PlotState(_caster, new SkillCasterUnit(7), new Unit(), null, null, skill);
        byte flag = 0;
        // Null event/target info is intentional: the ban must precede any effect selection.
        new PlotEventEffect().ApplyEffect(state, null, null, ref flag);
        await Assert.That(flag).IsEqualTo((byte)0);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(state.CancellationRequested()).IsTrue();
    }

    [Test]
    public async Task SourceItem_DoesNotTrustPacketTemplateOrAnotherUnit()
    {
        var itemCaster = new SkillItem { Type = SkillCasterType.Item, ObjId = 7, ItemTemplateId = 14677 };
        await Assert.That(ZoneSkillRestrictions.GetSourceItem(_caster, itemCaster)).IsNull();
        var skill = NewSkill();
        await Assert.That(skill.Use(_caster, itemCaster, new SkillCastUnitTarget(7), null, true, out _))
            .IsEqualTo(SkillResult.InvalidSource);
    }

    [Test]
    public async Task SourceItem_AcceptedTemplateAndCasterRemainAuthoritativeAfterItemRemoval()
    {
        var item = new Item
        {
            Id = 700,
            TemplateId = 150,
            Template = new ItemTemplate { Id = 150, UseSkillId = 15802 },
            OwnerId = 70,
            Count = 1
        };
        var character = CreateItemCharacter(item);
        var source = new SkillItem(70, item.Id, item.TemplateId);
        var skill = new Skill(new SkillTemplate { Id = 15802, TargetType = SkillTargetType.Self }, null);
        await Assert.That(skill.Use(character, source, new SkillCastUnitTarget(70), null, true, out var detail))
            .IsEqualTo(SkillResult.ZoneBanned);
        await Assert.That(detail).IsEqualTo(3u);
        await Assert.That(skill.SourceItemTemplateId).IsEqualTo(150u);
        await Assert.That(skill.OriginalCaster).IsSameReferenceAs(character);
        character.Inventory.Bag.Items.Clear();
        _caster.Transform.Local.SetPosition(130, 10, 100);
        await Assert.That(ZoneSkillRestrictions.CanApply(_caster, skill, source)).IsFalse();
        character.Transform.Local.SetPosition(130, 10, 100);
        await Assert.That(ZoneSkillRestrictions.CanApply(_caster, skill, source)).IsTrue();
    }

    [Test]
    [Arguments(11215u, 0, true)]
    [Arguments(16841u, 1, true)]
    [Arguments(11215u, 1, false)]
    [Arguments(16841u, 0, false)]
    [Arguments(16387u, 0, false)]
    [Arguments(12373u, 0, false)]
    public async Task PortalBook_OnlyConfirmedNativeActionsUseAnAlternateSkill(uint skillId, int portalId, bool expected)
    {
        var item = new Item
        {
            Id = 700,
            TemplateId = 4045,
            Template = new ItemTemplate { Id = 4045, UseSkillId = 11216, BindType = ItemBindType.BindOnPickup }
        };
        CreateItemCharacter(item);
        var source = new SkillItem(70, item.Id, item.TemplateId);
        var skill = new SkillTemplate { Id = skillId, TargetType = SkillTargetType.Self };
        var data = new SkillObjectSavePortalInfo { Flag = SkillObjectType.SavePortalInfo, Id = portalId, Name = "Home" };
        await Assert.That(SkillItemSource.CanUse(item, 70, source, skill, new SkillCastUnitTarget(70), data))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments("other-book")]
    [Arguments("wrong-template")]
    [Arguments("wrong-caster")]
    [Arguments("wrong-target")]
    [Arguments("doodad-target")]
    [Arguments("wrong-target-kind")]
    [Arguments("wrong-object")]
    public async Task PortalBook_RejectsAnUnconfirmedSourceOrShape(string mismatch)
    {
        var item = new Item
        {
            Id = 700,
            TemplateId = 4045,
            Template = new ItemTemplate { Id = 4045, UseSkillId = 11216, BindType = ItemBindType.BindOnPickup }
        };
        CreateItemCharacter(item);
        var source = new SkillItem(70, item.Id, item.TemplateId);
        var skill = new SkillTemplate { Id = 11215, TargetType = SkillTargetType.Self };
        SkillCastTarget target = new SkillCastUnitTarget(70);
        SkillObject data = new SkillObjectSavePortalInfo { Id = 0, Name = "Home" };
        switch (mismatch)
        {
            case "other-book": item.TemplateId = source.ItemTemplateId = 4046; break;
            case "wrong-template": source.ItemTemplateId = 4046; break;
            case "wrong-caster": source.ObjId = 71; break;
            case "wrong-target": target.ObjId = 71; break;
            case "doodad-target": target = new SkillCastDoodadTarget { ObjId = 70 }; break;
            case "wrong-target-kind": skill.TargetType = SkillTargetType.Doodad; break;
            case "wrong-object": data = new SkillObjectPortalInfo(); break;
        }
        await Assert.That(SkillItemSource.CanUse(item, 70, source, skill, target, data)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ForgedBoundItemPacket_FailsBeforeCostsForItemAndDefaultSkillRoutes(bool defaultRoute, bool unknownSkill)
    {
        var item = new Item
        {
            Id = 700,
            OwnerId = 70,
            Count = 1,
            TemplateId = 4046,
            Template = new ItemTemplate { Id = 4046, UseSkillId = 501, BindType = ItemBindType.BindOnPickup }
        };
        var character = CreateItemCharacter(item);
        var template = new SkillTemplate { Id = 500, TargetType = SkillTargetType.Self, ManaCost = 50, ConsumeLaborPower = 10 };
        SetField(SkillManager.Instance, "_skills", unknownSkill ? new Dictionary<uint, SkillTemplate>() : new() { [500] = template });
        SetField(SkillManager.Instance, "_defaultSkills", defaultRoute
            ? new Dictionary<uint, DefaultSkill> { [500] = new() { Template = template } } : []);
        SetField(SkillManager.Instance, "_commonSkills", new List<uint>());
        SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
        var source = new SkillItem(70, item.Id, item.TemplateId);
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = character };
        character.Connection = connection;
        var body = new PacketStream().Write(500u).Write(source).Write(new SkillCastUnitTarget(70)).Write((byte)0);
        var previousGcd = character.SkillLastUsed;
        var previousLabor = character.LaborPower;

        new CSStartSkillPacket { Connection = connection }.Read(body);

        await Assert.That(character.Mp).IsEqualTo(100);
        await Assert.That(character.LaborPower).IsEqualTo(previousLabor);
        await Assert.That(character.SkillLastUsed).IsEqualTo(previousGcd);
        await Assert.That(item.Count).IsEqualTo(1);
        var expected = unknownSkill ? SkillResult.InvalidSkill : SkillResult.InvalidSource;
        session.SendPacket(Is<byte[]>(packet => packet[^1] == (byte)expected)).WasCalled(Times.Once);
    }

    [Test]
    [Arguments(ItemBindType.Normal)]
    [Arguments(ItemBindType.BindOnPickup)]
    public async Task AuthoredItemSkill_DoesNotDependOnBindingStatus(ItemBindType bindType)
    {
        var item = new Item
        {
            Id = 700,
            TemplateId = 14677,
            Template = new ItemTemplate { Id = 14677, UseSkillId = 12373, BindType = bindType }
        };
        CreateItemCharacter(item);
        var source = new SkillItem(70, item.Id, item.TemplateId);
        var skill = new SkillTemplate { Id = 12373, TargetType = SkillTargetType.Self };
        await Assert.That(SkillItemSource.CanUse(item, 70, source, skill, new SkillCastUnitTarget(70), null)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MountedRuleFailure_SendsAuthoredDetailAndDoesNotRunRiderSkill(bool buffRule)
    {
        var character = CreateItemCharacter(new Item { Id = 700, TemplateId = 150 });
        character.AttachedPoint = AttachPointKind.Driver;
        var mate = new Mate { ObjId = 8, Mp = 100, Template = new NpcTemplate { Scale = 1 } };
        mate.Transform.Local.SetPosition(10, 10, 100);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(mate, _caster.ParentWorld);
        _caster.ParentWorld.AddObject(mate);
        var template = new SkillTemplate { Id = 12373, TargetType = SkillTargetType.Self, ManaCost = 50 };
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [12373] = template });
        SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
        var mates = new MateGameData();
        SetField(mates, "_mountSkills", new Dictionary<uint, MountSkills> { [1] = new() { Id = 1, SkillId = 12373 } });
        SetField(mates, "_mountAttachedSkills", new Dictionary<uint, MountAttachedSkills>
        {
            [1] = new() { Id = 1, MountSkillId = 1, AttachPointId = AttachPointKind.Driver, SkillId = 500 }
        });
        SetInstance(mates);
        if (buffRule)
        {
            using var data = SkillRequirementsGameDataTests.CreateConnection();
            SkillRequirementsGameDataTests.Execute(data,
                "INSERT INTO skill_reqs VALUES(13, 'f', NULL, 306, 't'); INSERT INTO skill_req_skills VALUES(13, 12373);");
            SetInstance(SkillRequirementsGameDataTests.Load(data));
            var buffs = Mock.Of<IBuffs>();
            buffs.CheckBuffTag(306).Returns(true);
            mate.Buffs = buffs.Object;
        }
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = character };
        character.Connection = connection;
        var body = new PacketStream().Write(12373u).Write(new SkillCasterMount(8))
            .Write(new SkillCastUnitTarget(8)).Write((byte)0);
        var expected = buffRule ? SkillResult.SkillReqFail : SkillResult.ZoneBanned;
        var expectedDetail = buffRule ? 13u : 21u;

        new CSStartSkillPacket { Connection = connection }.Read(body);

        await Assert.That(character.RiderSkills).IsEqualTo(0);
        await Assert.That(mate.Mp).IsEqualTo(100);
        session.SendPacket(Is<byte[]>(packet => packet[^5] == (byte)expected &&
            BitConverter.ToUInt32(packet, packet.Length - 4) == expectedDetail)).WasCalled(Times.Once);
    }

    [Test]
    public async Task DirectSpawnEffect_RejectsBeforeTemplateOrIdAllocation()
    {
        new SpawnEffect().Apply(_caster, new SkillCasterUnit(7), null, null, null,
            new EffectSource(NewSkill()), null, DateTime.UtcNow);
        await Assert.That(_caster.Mp).IsEqualTo(100);
    }

    [Test]
    public async Task DirectDoodadEffect_RejectsBeforePlacementAndTemplateAccess()
    {
        new SpawnDoodad().Execute(_caster, new SkillCasterUnit(7), null, null, null,
            NewSkill(), null, DateTime.UtcNow, 1, 0, 0, 0);
        await Assert.That(_caster.Mp).IsEqualTo(100);
    }

    [Test]
    public async Task DirectFishingEffect_RejectsDestinationZoneBeforeLootAccess()
    {
        var character = new ProbeCharacter { Id = 70, ObjId = 70 };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(character, _caster.ParentWorld);
        character.Transform.Local.SetPosition(130, 10, 100);
        var skill = new Skill(new SkillTemplate { Id = 21571 }, null);
        new FishingLoot().Execute(character, new SkillCasterUnit(70), _caster, null, null,
            skill, null, DateTime.UtcNow, 0, 0, 0, 0);
        await Assert.That(character.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task StartChanneling_RejectsBeforeBuffOrDoodadCreation()
    {
        var skill = NewSkill();
        skill.Template.ChannelingBuffId = 9000;
        skill.Template.ChannelingDoodadId = 9001;
        skill.StartChanneling(_caster, new SkillCasterUnit(7), _caster, null, null);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_caster.EndedSkills).IsEqualTo(1);
    }

    [Test]
    public async Task EndChanneling_MarksZoneFailureBeforeEndCostsAndItemUse()
    {
        var skill = NewSkill();
        skill.EndChanneling(_caster, null, new SkillCasterUnit(7));
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(_caster.EndedSkills).IsEqualTo(1);
    }

    [Test]
    public async Task DelayedZoneCheck_UsesTemplateIdInsteadOfMutableSkillId()
    {
        var skill = NewSkill();
        skill.Id = 2;
        await Assert.That(ZoneSkillRestrictions.CanApply(_caster, skill, new SkillCasterUnit(7))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlaveDestination_UsesOwnerForAbsentOrOriginOverride(bool originOverride)
    {
        using var positionOverride = originOverride ? new Transform(null) : null;
        var source = new PositionAndRotation(130, 10, 100, 0, 0, 0);
        var expected = source.Clone();
        expected.AddDistanceToFront(5);
        await Assert.That(SlaveManager.GetItemSpawnDestination(source, null, positionOverride, 1))
            .IsEqualTo(expected.Position);
        await Assert.That(source.Position).IsEqualTo(new Vector3(130, 10, 100));
    }

    [Test]
    public async Task SlaveDestination_UsesExplicitOverrideWithoutSpawnOffset()
    {
        using var positionOverride = new Transform(null);
        positionOverride.Local.SetPosition(70, 10, 100);
        await Assert.That(SlaveManager.GetItemSpawnDestination(_caster.Transform.World, null, positionOverride, 50))
            .IsEqualTo(new Vector3(70, 10, 100));
    }

    [Test]
    [Arguments(SkillResult.SkillReqFail, 25u)]
    [Arguments(SkillResult.ZoneBanned, 21u)]
    public async Task FailurePacket_PreservesConfirmedByteAndAuthoredRecordId(SkillResult result, uint id)
    {
        var packet = new SCSkillStartedPacket(12373, 0, new SkillCasterUnit(7), new SkillCastUnitTarget(7), NewSkill(), new SkillObject())
            .SetSkillResult(result).SetResultUInt(id);
        var bytes = packet.Write(new PacketStream()).GetBytes();
        await Assert.That(bytes[^6]).IsEqualTo((byte)5);
        await Assert.That(bytes[^5]).IsEqualTo((byte)result);
        await Assert.That(BitConverter.ToUInt32(bytes, bytes.Length - 4)).IsEqualTo(id);
    }

    [Test]
    public async Task Reload_ClearsOldZoneRules()
    {
        using var connection = CreateData();
        SkillRequirementsGameDataTests.Execute(connection, "DELETE FROM zone_group_banned_tags;");
        _zones.LoadBannedTags(connection);
        await Assert.That(_zones.IsTagBanned(45, 296)).IsFalse();
        await Assert.That(_zones.HasBannedTags).IsFalse();
    }

    private static Skill NewSkill() => new(new SkillTemplate
    {
        Id = 12373,
        TargetType = SkillTargetType.Self,
        CancelOngoingBuffs = true,
        ManaCost = 50
    }, null);

    private static SqliteConnection CreateData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SkillRequirementsGameDataTests.Execute(connection, """
            CREATE TABLE zone_group_banned_tags(id INTEGER, zone_group_id INTEGER, tag_id INTEGER, banned_periods_id INTEGER);
            INSERT INTO zone_group_banned_tags VALUES
                (2,45,471,0),(3,45,472,0),(13,45,628,0),(16,45,629,0),(20,49,628,0),(21,45,296,0),
                (160,49,1216,0),(185,45,1348,0),(186,49,1348,0),(203,63,1338,24);
            CREATE TABLE tagged_skills(tag_id INTEGER, skill_id INTEGER);
            INSERT INTO tagged_skills VALUES(296,12373),(1348,21571);
            CREATE TABLE tagged_items(tag_id INTEGER, item_id INTEGER);
            INSERT INTO tagged_items VALUES(471,820),(472,150),(1216,150),(628,1449),(629,14677);
            CREATE TABLE tagged_buffs(tag_id INTEGER, buff_id INTEGER);
            CREATE TABLE tagged_npcs(tag_id INTEGER, npc_id INTEGER);
            """);
        return connection;
    }

    private ProbeCharacter CreateItemCharacter(Item item)
    {
        var itemManager = new ItemManager(null, null, null, null, null, null);
        var character = new ProbeCharacter { Id = 70, ObjId = 70, NumInventorySlots = 10, Mp = 100 };
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var slotType in Enum.GetValues<SlotType>())
        {
            if (slotType == SlotType.EquipmentMate) continue;
            var container = new ItemContainer(character.Id, slotType, false, character)
            { ContainerId = (ulong)containers.Count + 1, Owner = character };
            containers.Add(container.ContainerId, container);
        }
        SetField(itemManager, "_allPersistentContainers", containers);
        SetField(itemManager, "_allItems", new Dictionary<ulong, Item> { [item.Id] = item });
        SetInstance(itemManager);
        character.Inventory = new Inventory(character);
        character.Inventory.Bag.Items.Add(item);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(character, _caster.ParentWorld);
        character.Transform.Local.SetPosition(10, 10, 100);
        return character;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ProbeUnit : Unit
    {
        public int EndedSkills { get; private set; }
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnSkillEnd(Skill skill) => EndedSkills++;
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
    }

    private sealed class ProbeCharacter() : Character(null)
    {
        public int RiderSkills { get; private set; }
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
        public override SkillResult UseSkill(uint skillId, IUnit target)
        {
            RiderSkills++;
            return SkillResult.Success;
        }
    }
}
