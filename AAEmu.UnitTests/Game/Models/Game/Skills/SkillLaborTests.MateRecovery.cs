using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(KillReason.Damage)]
    [Arguments(KillReason.Fall)]
    [Arguments(KillReason.Gm)]
    public async Task MateInjury_LethalDamageKeepsLivingHealthWithoutDeathOrKillEvents(KillReason reason)
    {
        var (world, mate, item, activeBuffs) = PrepareMateRecovery();
        var deaths = 0;
        var worldKills = 0;
        var ownerKills = 0;
        mate.Events.OnDeath += (_, _) => deaths++;
        world.Events.OnUnitKilled += (_, _) => worldKills++;
        _owner.Events.OnKill += (_, _) => ownerKills++;
        mate.CurrentTarget = _owner;
        mate.IsInBattle = true;

        mate.ReduceCurrentHp(_owner, 40, reason);
        await Assert.That(mate.Hp).IsEqualTo(60);
        await Assert.That(mate.IsInjured).IsFalse();
        mate.ReduceCurrentHp(_owner, 60, reason);
        mate.ReduceCurrentHp(_owner, 1000, reason);

        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.IsDead).IsFalse();
        await Assert.That(mate.IsInjured && mate.IsDowned).IsTrue();
        await Assert.That(item.DetailInjured && item.IsDirty).IsTrue();
        await Assert.That(mate.DbInfo.Hp).IsEqualTo(1);
        await Assert.That(mate.DbInfo.Mp).IsEqualTo(80);
        await Assert.That(activeBuffs).IsEquivalentTo(new[] { Mate.InjuryBuffId, Mate.DownedBuffId });
        await Assert.That(mate.IsInBattle).IsFalse();
        await Assert.That(mate.CurrentTarget).IsNull();
        await Assert.That(deaths + worldKills + ownerKills).IsEqualTo(0);
        await Assert.That(_owner.Packets.OfType<SCUnitDeathPacket>()).IsEmpty();
    }

    [Test]
    public async Task MateInjury_DirectDeathCallbackUsesTheSameLivingInjuryTransition()
    {
        var (_, mate, item, _) = PrepareMateRecovery();
        var deaths = 0;
        mate.Events.OnDeath += (_, _) => deaths++;
        mate.Hp = 0;

        mate.DoDie(_owner, KillReason.Gm);
        mate.DoDie(_owner, KillReason.Gm);

        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.IsInjured && mate.IsDowned && item.DetailInjured).IsTrue();
        await Assert.That(deaths).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MateInjury_OrdinaryHealthWritesCannotHealEitherInjuryState(bool downed)
    {
        var (_, mate, item, _) = PrepareMateRecovery();
        item.DetailInjured = true;
        mate.RestoreInjuryState(item, 1, downed);

        mate.Hp = 100;
        await Assert.That(mate.Hp).IsEqualTo(1);
        mate.Hp += 25;
        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.IsDowned).IsEqualTo(downed);
    }

    [Test]
    [Arguments(false, 0)]
    [Arguments(false, -10)]
    [Arguments(true, 70)]
    public async Task MateInjury_RestoreNormalizesLegacyDeadHealthAndPersistentInjury(bool detailInjured, int savedHp)
    {
        var (_, mate, item, _) = PrepareMateRecovery();
        item.DetailInjured = detailInjured;
        item.IsDirty = false;
        mate.Hp = savedHp;

        mate.RestoreInjuryState(item, savedHp, false);

        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.IsInjured).IsTrue();
        await Assert.That(mate.IsDowned).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(mate.DbInfo.Hp).IsEqualTo(1);
    }

    [Test]
    public async Task MateInjury_TemporarySummonKeepsTheNormalDeathPath()
    {
        var (world, mate, item, activeBuffs) = PrepareMateRecovery(temporary: true);
        var deaths = 0;
        var worldKills = 0;
        var ownerKills = 0;
        mate.Events.OnDeath += (_, _) => deaths++;
        world.Events.OnUnitKilled += (_, _) => worldKills++;
        _owner.Events.OnKill += (_, _) => ownerKills++;

        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);

        await Assert.That(mate.Hp).IsEqualTo(0);
        await Assert.That(mate.IsInjured || mate.IsDowned || item.DetailInjured).IsFalse();
        await Assert.That(mate.SummonItem).IsNull();
        await Assert.That(activeBuffs).IsEmpty();
        await Assert.That(deaths).IsEqualTo(1);
        await Assert.That(worldKills).IsEqualTo(1);
        await Assert.That(ownerKills).IsEqualTo(1);
        await Assert.That(_owner.Packets.OfType<SCUnitDeathPacket>()).HasSingleItem();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MateRecovery_GetUpChangesOnlyDownedStateAfterCommit(bool commit)
    {
        var (_, mate, item, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        item.IsDirty = false;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => commit;

        var accepted = SkillLaborBatch.Run(_owner, skill, true,
            () => mate.StageRecovery(SkillLaborBatch.Current, true, 100, 200));

        await Assert.That(accepted).IsEqualTo(commit);
        await Assert.That(mate.IsInjured && item.DetailInjured).IsTrue();
        await Assert.That(mate.IsDowned).IsEqualTo(!commit);
        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.Mp).IsEqualTo(80);
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(activeBuffs.Contains(Mate.InjuryBuffId)).IsTrue();
        await Assert.That(activeBuffs.Contains(Mate.DownedBuffId)).IsEqualTo(!commit);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(commit ? 10 : 20));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MateRecovery_TreatmentAndInventoryShareTheCommitOrRestoreAllState(bool commit)
    {
        var (_, mate, item, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        item.IsDirty = false;
        var oldUpdatedAt = mate.DbInfo.UpdatedAt;
        var skill = NewSkill();
        skill.CommitLaborBatch = (_, _) => commit;

        var accepted = SkillLaborBatch.Run(_owner, skill, true, () =>
        {
            SkillLaborBatch.Current.Consume(_owner.Inventory.Bag, _material.TemplateId, 1, _material);
            mate.StageRecovery(SkillLaborBatch.Current, false, 200, 40);
        });

        await Assert.That(accepted).IsEqualTo(commit);
        await Assert.That(mate.IsInjured || mate.IsDowned).IsEqualTo(!commit);
        await Assert.That(item.DetailInjured).IsEqualTo(!commit);
        await Assert.That(item.IsDirty).IsEqualTo(commit);
        await Assert.That(mate.Hp).IsEqualTo(commit ? 200 : 1);
        await Assert.That(mate.Mp).IsEqualTo(commit ? 40 : 80);
        await Assert.That(mate.DbInfo.Hp).IsEqualTo(mate.Hp);
        await Assert.That(mate.DbInfo.Mp).IsEqualTo(mate.Mp);
        await Assert.That(mate.DbInfo.UpdatedAt).IsEqualTo(oldUpdatedAt);
        await Assert.That(activeBuffs.Count).IsEqualTo(commit ? 0 : 2);
        await Assert.That(_material.Count).IsEqualTo(commit ? 2 : 3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(commit ? 10 : 20));
    }

    [Test]
    public async Task MateRecovery_AttackAfterGetUpTripsTheInjuredMateAgain()
    {
        var (_, mate, _, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        var skill = NewSkill();
        SkillLaborBatch.Run(_owner, skill, true,
            () => mate.StageRecovery(SkillLaborBatch.Current, true, 1, 80));
        await Assert.That(mate.IsDowned).IsFalse();

        mate.ReduceCurrentHp(_owner, 1, KillReason.Gm);

        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.IsDowned).IsTrue();
        await Assert.That(activeBuffs).IsEquivalentTo(new[] { Mate.InjuryBuffId, Mate.DownedBuffId });
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MateRecovery_GetUpSkillSettlesOnceAndPreservesInjury(bool commit)
    {
        var (_, mate, item, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        var skill = NewMateRecoverySkill(mate, getUp: true);
        var commits = 0;
        skill.CommitLaborBatch = (_, _) => { commits++; return commit; };

        skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), mate, new SkillCastUnitTarget(mate.ObjId), null);
        skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), mate, new SkillCastUnitTarget(mate.ObjId), null);

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(skill.Cancelled).IsEqualTo(!commit);
        await Assert.That(mate.IsInjured && item.DetailInjured).IsTrue();
        await Assert.That(mate.IsDowned).IsEqualTo(!commit);
        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.Mp).IsEqualTo(80);
        await Assert.That(activeBuffs.Contains(Mate.DownedBuffId)).IsEqualTo(!commit);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MateRecovery_PotionSkillTreatsInjuryAndConsumesOnePotionOnlyAfterCommit(bool commit)
    {
        var (_, mate, item, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        item.IsDirty = false;
        var skill = NewMateRecoverySkill(mate, getUp: false);
        var commits = 0;
        var uses = 0;
        _owner.ConditionChance = true;
        _owner.Events.OnItemUse += (_, _) => uses++;
        skill.CommitLaborBatch = (_, _) => { commits++; return commit; };
        var source = new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId);

        skill.ApplyEffects(_owner, source, mate, new SkillCastUnitTarget(mate.ObjId), null);
        skill.ApplyEffects(_owner, source, mate, new SkillCastUnitTarget(mate.ObjId), null);

        await Assert.That(commits).IsEqualTo(1);
        await Assert.That(skill.Cancelled).IsEqualTo(!commit);
        await Assert.That(mate.IsInjured || mate.IsDowned).IsEqualTo(!commit);
        await Assert.That(item.DetailInjured).IsEqualTo(!commit);
        await Assert.That(item.IsDirty).IsEqualTo(commit);
        await Assert.That(mate.Hp).IsEqualTo(commit ? 201 : 1);
        await Assert.That(mate.Mp).IsEqualTo(commit ? 120 : 80);
        await Assert.That(mate.DbInfo.Hp).IsEqualTo(mate.Hp);
        await Assert.That(mate.DbInfo.Mp).IsEqualTo(mate.Mp);
        await Assert.That(activeBuffs.Count).IsEqualTo(commit ? 0 : 2);
        await Assert.That(_material.Count).IsEqualTo(commit ? 2 : 3);
        await Assert.That(uses).IsEqualTo(commit ? 1 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
    }

    [Test]
    public async Task MateRecovery_PotionSkillRestoresHealthyMateAndCapsBothResources()
    {
        var (_, mate, _, _) = PrepareMateRecovery();
        mate.Hp = 950;
        mate.Mp = 195;
        var skill = NewMateRecoverySkill(mate, getUp: false);

        skill.ApplyEffects(_owner, new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId),
            mate, new SkillCastUnitTarget(mate.ObjId), null);

        await Assert.That(skill.Cancelled).IsFalse();
        await Assert.That(mate.Hp).IsEqualTo(1000);
        await Assert.That(mate.Mp).IsEqualTo(200);
        await Assert.That(_material.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("wrong-owner")]
    [Arguments("wrong-item-owner")]
    [Arguments("stale-target")]
    [Arguments("stale-database-record")]
    [Arguments("untracked")]
    [Arguments("temporary")]
    [Arguments("wrong-source")]
    [Arguments("wrong-source-template")]
    [Arguments("out-of-range")]
    public async Task MateRecovery_PotionRejectsInvalidCompletionWithoutCostsOrStateChanges(string reason)
    {
        var (world, mate, item, activeBuffs) = PrepareMateRecovery();
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        item.IsDirty = false;
        var skill = NewMateRecoverySkill(mate, getUp: false);
        SkillCaster source = new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId);
        var commits = 0;
        skill.CommitLaborBatch = (_, _) => { commits++; return true; };
        switch (reason)
        {
            case "wrong-owner": mate.OwnerObjId = 99; break;
            case "wrong-item-owner": item.OwnerId = 99; break;
            case "stale-target": skill.InitialTarget = new Mate { ObjId = mate.ObjId }; break;
            case "stale-database-record":
                SetField(_owner.Mates, "_mates", new Dictionary<ulong, MateDb> { [item.Id] = new() });
                break;
            case "untracked": world.MateManager.TryBeginMateRemoval(_owner.Id, mate); break;
            case "temporary": mate.IsTemporarySummon = true; break;
            case "wrong-source": source = new SkillCasterUnit(_owner.ObjId); break;
            case "wrong-source-template": source = new SkillItem(_owner.ObjId, _material.Id, 999); break;
            case "out-of-range": mate.Transform.Local.SetPosition(100, 10, 10); break;
        }
        var itemWasDirty = item.IsDirty;

        skill.ApplyEffects(_owner, source, mate, new SkillCastUnitTarget(mate.ObjId), null);

        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(commits).IsEqualTo(0);
        await Assert.That(mate.IsInjured && mate.IsDowned && item.DetailInjured).IsTrue();
        await Assert.That(item.IsDirty).IsEqualTo(itemWasDirty);
        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(mate.Mp).IsEqualTo(80);
        await Assert.That(activeBuffs.Count).IsEqualTo(2);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
    }

    [Test]
    public async Task MateRecovery_GetUpRequiresOwnedDownedMateAndTheOwnersUnitSource()
    {
        var (_, mate, _, _) = PrepareMateRecovery();
        var skill = NewMateRecoverySkill(mate, getUp: true);
        var target = new SkillCastUnitTarget(mate.ObjId);
        await Assert.That(MateRecovery.Check(_owner, new SkillCasterUnit(_owner.ObjId), target, skill))
            .IsEqualTo(SkillResult.InvalidTarget);
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        await Assert.That(MateRecovery.Check(_owner, new SkillCasterUnit(999), target, skill))
            .IsEqualTo(SkillResult.InvalidTarget);
        await Assert.That(MateRecovery.Check(_owner, new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId), target, skill))
            .IsEqualTo(SkillResult.InvalidTarget);
        await Assert.That(MateRecovery.Check(_owner, new SkillCasterUnit(_owner.ObjId), target, skill))
            .IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MateInjury_LethalFallStillReachesTheRiderAfterAutomaticDetachment(bool alreadyInjured)
    {
        var (world, mate, item, _) = PrepareMateRecovery();
        if (alreadyInjured)
        {
            item.DetailInjured = true;
            mate.RestoreInjuryState(item, 1, false);
        }
        var rider = new MateRecoveryRider
        {
            Id = 8, ObjId = 80, Hp = 100, ParentWorld = world,
            IsRiding = true, AttachedPoint = AttachPointKind.Driver
        };
        rider.Transform.Parent = mate.Transform;
        mate.Passengers[AttachPointKind.Driver]._objId = rider.ObjId;
        world.AddObject(rider);

        var damage = mate.DoFallDamage(32000);

        await Assert.That(damage).IsEqualTo(alreadyInjured ? 0 : 99);
        await Assert.That(mate.IsDowned).IsTrue();
        await Assert.That(mate.Hp).IsEqualTo(1);
        await Assert.That(rider.FallImpacts).IsEquivalentTo(new ushort[] { 32000 });
        await Assert.That(rider.IsRiding).IsFalse();
        await Assert.That(rider.Transform.Parent).IsNull();
        await Assert.That(mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(0u);
    }

    private sealed class MateRecoveryRider : CharacterMock
    {
        public List<ushort> FallImpacts { get; } = [];
        public override int DoFallDamage(ushort impact)
        {
            FallImpacts.Add(impact);
            return 0;
        }

        public override void SetPosition(float x, float y, float z, float rotationX, float rotationY, float rotationZ)
        {
            Transform.Local.SetPosition(x, y, z);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MateRecovery_MountRequestWaitsForGetUpSettlement(bool commit)
    {
        var (world, mate, _, _) = PrepareMateRecovery();
        var seats = new MateSeatGameData();
        SetField(seats, "_seats", new Dictionary<uint, HashSet<AttachPointKind>>
            { [mate.ModelId] = [AttachPointKind.Driver] });
        SetInstance(seats);
        mate.ReduceCurrentHp(_owner, 100, KillReason.Gm);
        var skill = NewMateRecoverySkill(mate, getUp: true);
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _owner };
        using var enteredCommit = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        using var enteredMount = new ManualResetEventSlim();
        using var completedMount = new ManualResetEventSlim();
        skill.CommitLaborBatch = (_, _) =>
        {
            enteredCommit.Set();
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the recovery commit.");
            return commit;
        };
        var recovery = Task.Run(() => skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId),
            mate, new SkillCastUnitTarget(mate.ObjId), null));
        Task mount = Task.CompletedTask;
        try
        {
            await Assert.That(enteredCommit.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            mount = Task.Run(() =>
            {
                enteredMount.Set();
                world.MateManager.MountMate(connection, mate.TlId, AttachPointKind.Driver, AttachUnitReason.None);
                completedMount.Set();
            });
            await Assert.That(enteredMount.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(completedMount.Wait(TimeSpan.FromMilliseconds(100))).IsFalse();
            await Assert.That(_owner.IsRiding).IsFalse();
        }
        finally
        {
            releaseCommit.Set();
            await Task.WhenAll(recovery, mount).WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(mate.IsDowned).IsEqualTo(!commit);
        await Assert.That(_owner.IsRiding).IsEqualTo(commit);
        await Assert.That(mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(commit ? _owner.ObjId : 0u);
        await Assert.That(ReferenceEquals(_owner.Transform.Parent, mate.Transform)).IsEqualTo(commit);
    }

    private Skill NewMateRecoverySkill(Mate mate, bool getUp)
    {
        var template = new SkillTemplate
        {
            Id = getUp ? 13719u : 15220u,
            TargetType = getUp ? SkillTargetType.Self : SkillTargetType.Friendly,
            TargetSelection = SkillTargetSelection.Target,
            TargetRelation = getUp ? SkillTargetRelation.Friendly : SkillTargetRelation.Any,
            TargetAlive = true, SourceAlive = true, MaxRange = getUp ? 4 : 3,
            Effects = [new SkillEffect
            {
                EffectId = getUp ? 8380u : 12554u,
                Template = new SpecialEffect
                {
                    SpecialEffectTypeId = getUp ? SpecialType.MateMakeGetUp : SpecialType.HealPet,
                    Value1 = getUp ? 0 : 20, Value2 = getUp ? 0 : 20
                },
                StartLevel = 0, EndLevel = 99, Friendly = true, NonFriendly = true,
                Front = true, Back = true, Chance = 100,
                ApplicationMethod = SkillEffectApplicationMethod.Target, ConsumeItemCount = 1
            }]
        };
        if (!getUp)
        {
            _material.Template.UseSkillId = template.Id;
            _material.Template.UseSkillAsReagent = true;
        }
        return new Skill(template) { InitialTarget = mate, CommitLaborBatch = (_, _) => true };
    }

    private (WorldInstance World, Mate Mate, SummonMate Item, HashSet<uint> ActiveBuffs)
        PrepareMateRecovery(bool temporary = false)
    {
        var manager = new WorldManager(null, null, null, null, null);
        SetInstance(manager);
        SetInstance(new ZoneManager(null, null));
        SetInstance(new UnitAttributeLimitsGameData());
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetInstance(models);
        var requirements = new SkillRequirementsGameData();
        using (var connection = new SqliteConnection("Data Source=:memory:"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE skill_reqs(id INTEGER, target TEXT, buff_id INTEGER, buff_tag_id INTEGER, default_result TEXT);
                CREATE TABLE skill_req_skills(skill_req_id INTEGER, skill_id INTEGER);
                CREATE TABLE skill_req_skill_tags(skill_req_id INTEGER, skill_tag_id INTEGER);
                INSERT INTO skill_reqs VALUES(37, 't', 0, 371, 'f');
                INSERT INTO skill_req_skills VALUES(37, 15220);
                """;
            command.ExecuteNonQuery();
            requirements.Load(connection);
        }
        SetInstance(requirements);
        var world = new WorldInstance(new WorldTemplate
        { CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 0)
        { Regions = new Region[16, 16] };
        world.Regions[0, 0] = new Region(world, 0, 0, 0);
        world.MateManager = new MateManager(world);
        world.Water.OceanLevel = -1000;
        SetField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = world });
        _owner.ParentWorld = world;
        _owner.Transform.Local.SetPosition(10, 10, 10);
        world.AddObject(_owner);
        var item = new SummonMate
        {
            Id = 2, TemplateId = 301, Template = new SummonMateTemplate { Id = 301 },
            Count = 1, OwnerId = _owner.Id, SlotType = SlotType.Inventory, Slot = 1,
            _holdingContainer = _owner.Inventory.Bag
        };
        _items.Add(item.Id, item);
        _owner.Inventory.Bag.Items.Add(item);
        _owner.Inventory.Bag.UpdateFreeSlotCount();
        var mate = new Mate
        {
            Id = 17, ObjId = 71, TlId = 31, OwnerObjId = _owner.ObjId, OwnerId = _owner.Id, ItemId = item.Id,
            IsTemporarySummon = temporary, Hp = 100, Mp = 80, ParentWorld = world,
            Template = new NpcTemplate { Id = 501 },
            DbInfo = new MateDb
            {
                Id = 17, ItemId = item.Id, Owner = _owner.Id, Hp = 100, Mp = 80,
                UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };
        mate.Transform.Local.SetPosition(11, 10, 10);
        var activeBuffs = new HashSet<uint>();
        var buffs = Mock.Of<IBuffs>();
        buffs.GetAbsorptionEffects().Returns(Array.Empty<Buff>());
        buffs.CheckBuffTag(371).Returns(() => activeBuffs.Contains(Mate.DownedBuffId));
        var templates = new Dictionary<uint, BuffTemplate>();
        foreach (var id in new[] { Mate.InjuryBuffId, Mate.DownedBuffId })
        {
            templates[id] = new BuffTemplate { Id = id };
            buffs.CheckBuff(id).Returns(() => activeBuffs.Contains(id));
            buffs.AddBuff(Is<Buff>(buff => buff.Template.Id == id), 0, 0).Callback(() => { activeBuffs.Add(id); });
            buffs.RemoveBuff(id).Callback(() => { activeBuffs.Remove(id); });
        }
        mate.Buffs = buffs.Object;
        SetField(SkillManager.Instance, "_buffs", templates);
        SetField(SkillManager.Instance, "_taggedBuffs", new Dictionary<uint, List<uint>>
            { [371] = [Mate.DownedBuffId] });
        var formulas = new Dictionary<UnitFormulaKind, UnitFormula>();
        foreach (var kind in Enum.GetValues<UnitFormulaKind>())
        {
            var formula = new UnitFormula();
            Func<Dictionary<string, double>, double> expression = _ => kind switch
            {
                UnitFormulaKind.MaxHealth => 1000,
                UnitFormulaKind.MaxMana => 200,
                _ => 0
            };
            typeof(Formula).GetProperty("Expression", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(formula, expression);
            formulas[kind] = formula;
        }
        SetField(FormulaManager.Instance, "_unitFormulas",
            new Dictionary<FormulaOwnerType, Dictionary<UnitFormulaKind, UnitFormula>> { [FormulaOwnerType.Mate] = formulas });
        SetField(FormulaManager.Instance, "_unitVariables",
            new Dictionary<uint, Dictionary<UnitFormulaVariableType, Dictionary<uint, UnitFormulaVariable>>>());
        mate.RestoreInjuryState(item, 100, false);
        _owner.Mates = new CharacterMates(_owner);
        SetField(_owner.Mates, "_mates", new Dictionary<ulong, MateDb> { [item.Id] = mate.DbInfo });
        world.AddObject(mate);
        world.MateManager.TrackActiveMate(_owner.Id, mate);
        return (world, mate, item, activeBuffs);
    }
}
