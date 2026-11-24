using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

[NotInParallel]
public sealed class NpcHonorTests
{
    private readonly List<(FieldInfo Field, object Previous)> _singletons = [];
    private readonly ConcurrentDictionary<uint, WorldInstance> _worlds = [];
    private readonly ConcurrentDictionary<uint, Team> _teams = [];
    private IServiceProvider _previousServices;
    private ServiceProvider _services;
    private WorldConfig _settings;

    [Before(Test)]
    public void SetUp()
    {
        _settings = new WorldConfig { HonorRate = 1, TagShareEnabled = true };
        _previousServices = SingletonContainer.ServiceProvider;
        _services = new ServiceCollection()
            .AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration { World = _settings }))
            .BuildServiceProvider();
        SingletonContainer.ServiceProvider = _services;
        var worldManager = new WorldManager(null, null, null, null, null);
        SetField(worldManager, "_worlds", _worlds);
        SetField(worldManager, "_characters", new ConcurrentDictionary<uint, Character>());
        ReplaceSingleton(worldManager);
        var teamManager = new TeamManager(null, null, null);
        SetField(teamManager, "_activeTeams", _teams);
        ReplaceSingleton(teamManager);
        ReplaceSingleton(new QuestManager(null, Mock.Of<IZoneManager>().Object));
        ReplaceSingleton(new AchievementGameData());
        ReplaceSingleton(new UnitAttributeLimitsGameData());
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        ReplaceSingleton(models);
        var items = new ItemManager(null, null, null, null, null, null);
        SetField(items, "_lootPackDroppingNpc", new Dictionary<uint, List<LootPackDroppingNpc>>());
        ReplaceSingleton(items);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var world in _worlds.Values)
            GC.SuppressFinalize(world);
        foreach (var (field, previous) in _singletons)
            field.SetValue(null, previous);
        _singletons.Clear();
        _worlds.Clear();
        _teams.Clear();
        SingletonContainer.ServiceProvider = _previousServices;
        _services.Dispose();
    }

    [Test]
    [Arguments(6402u)]
    [Arguments(6403u)]
    public async Task DoDie_SuspiciousGuardSummonsOneSpellbookAtItsDeathPosition(uint guardTemplateId)
    {
        // r208022 np_skills 7879 and 7880 use effect 23551 to summon the Guard's Spellbook.
        const uint skillId = 19315;
        const uint bookId = 5529;
        var template = new SkillTemplate
        {
            Id = skillId, DefaultGcd = true, SourceAlive = false, SourceDead = true,
            TargetType = SkillTargetType.Self, TargetAlive = true, MaxRange = 4,
            Effects =
            [
                new SkillEffect
                {
                    EffectId = 23551, ApplicationMethod = SkillEffectApplicationMethod.Source,
                    StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true, Chance = 100,
                    Template = new InteractionEffect { WorldInteraction = WorldInteractionType.SummonDoodad, DoodadId = bookId }
                }
            ]
        };
        var skills = new SkillManager(null, null);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = template });
        SetField(skills, "_skillReagents", new Dictionary<uint, SkillReagent>());
        SetField(skills, "_skillProducts", new Dictionary<uint, SkillProduct>());
        ReplaceSingleton(skills);
        ReplaceSingleton(new DuelManager());
        ReplaceSingleton(new ZoneManager(null, null));
        ReplaceSingleton(new UnitRequirementsGameData());
        ReplaceSingleton(new SkillRequirementsGameData());
        var npcs = new NpcGameData();
        SetField(npcs, "<SkillsForNpc>k__BackingField", new Dictionary<uint, List<NpcSkill>>
        {
            [guardTemplateId] = [new() { SkillId = skillId, SkillUseCondition = SkillUseConditionKind.OnDeath }]
        });
        ReplaceSingleton(npcs);
        var ids = new ObjectIdManager();
        ids.Initialize();
        var doodads = new DoodadManager(ids, null, null, null, null);
        SetField(doodads, "_templates", new Dictionary<uint, DoodadTemplate> { [bookId] = new() { Id = bookId } });
        ReplaceSingleton(doodads);
        var world = CreateWorld(1);
        var guard = new QuietNpc
        {
            ObjId = 100, TemplateId = guardTemplateId, Level = 30, Hp = 100,
            Template = new NpcTemplate(), ParentWorld = world
        };
        world.AddObject(guard);
        guard.Transform.Local.SetPosition(42, 55, 23);
        guard.RegisterNpcEvents();
        var player = CreatePlayer(world, 1);

        guard.ReduceCurrentHp(player, 100);
        await Assert.That(world.GetAllDoodads().Count).IsEqualTo(1);

        // The AI also sends the death event when it enters its dead behavior.
        guard.Events.OnDeath(guard, new OnDeathArgs { Victim = guard, Killer = guard });

        var book = world.GetAllDoodads().Single();
        await Assert.That(book.TemplateId).IsEqualTo(bookId);
        await Assert.That(book.OwnerObjId).IsEqualTo(guard.ObjId);
        await Assert.That(book.Transform.World.Position).IsEqualTo(guard.Transform.World.Position);
    }

    [Test]
    public async Task DoDie_KrakenTag_GrantsAuthoredHonorWithoutAnExperienceLevelPenalty()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        npc.CharacterTagging.AddTagger(player, 100);

        npc.ReduceCurrentHp(player, 100);

        await Assert.That(player.HonorPoint).IsEqualTo(100);
        await Assert.That(player.Experience).IsEqualTo(0);
        await Assert.That(npc.Hp).IsEqualTo(0);
    }

    [Test]
    [Arguments(100)]
    [Arguments(175)]
    public async Task DamageEffect_OneHitKill_TagsBeforeDeathAndGrantsLootAndQuestCredit(int damage)
    {
        var npc = CreateKraken();
        PrepareDamageAndLoot(npc);
        var player = CreatePlayer(npc.ParentWorld, 1);
        var quest = CreateHuntObjective(player, npc.TemplateId);
        Character taggerAtDeath = null;
        var damageAtDeath = 0;
        npc.Events.OnDeath += (_, _) =>
        {
            taggerAtDeath = npc.CharacterTagging.Tagger;
            damageAtDeath = RecordedTagDamage(npc, player);
        };

        DealDamage(player, npc, damage);

        await Assert.That(npc.Hp).IsEqualTo(0);
        await Assert.That(taggerAtDeath).IsSameReferenceAs(player);
        await Assert.That(damageAtDeath).IsEqualTo(100);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(LootRecipients(npc).Select(character => character.Id)).IsEquivalentTo([player.Id]);
        await Assert.That(player.HonorPoint).IsEqualTo(100);
    }

    [Test]
    public async Task DamageEffect_OverkillOnlyCreditsRemainingHealthAndZeroDamageDoesNotContribute()
    {
        var npc = CreateKraken();
        PrepareDamageAndLoot(npc);
        var tagger = CreatePlayer(npc.ParentWorld, 1);
        var finisher = CreatePlayer(npc.ParentWorld, 2);
        var zeroDamagePlayer = CreatePlayer(npc.ParentWorld, 3);
        var taggerQuest = CreateHuntObjective(tagger, npc.TemplateId);
        var finisherQuest = CreateHuntObjective(finisher, npc.TemplateId);
        var zeroDamageQuest = CreateHuntObjective(zeroDamagePlayer, npc.TemplateId);
        var finisherDamageAtDeath = 0;
        npc.Events.OnDeath += (_, _) => finisherDamageAtDeath = RecordedTagDamage(npc, finisher);

        DealDamage(tagger, npc, 60);
        await Assert.That(npc.Hp).IsEqualTo(40);
        DealDamage(zeroDamagePlayer, npc, 0);
        await Assert.That(npc.Hp).IsEqualTo(40);
        DealDamage(finisher, npc, 175);

        await Assert.That(npc.Hp).IsEqualTo(0);
        await Assert.That(finisherDamageAtDeath).IsEqualTo(40);
        await Assert.That(taggerQuest.Objectives[0]).IsEqualTo(1);
        await Assert.That(finisherQuest.Objectives[0]).IsEqualTo(1);
        await Assert.That(zeroDamageQuest.Objectives[0]).IsEqualTo(0);
        await Assert.That(LootRecipients(npc).Select(character => character.Id)).IsEquivalentTo([tagger.Id]);
        await Assert.That(tagger.HonorPoint).IsEqualTo(100);
        await Assert.That(finisher.HonorPoint).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, false)]
    public async Task DamageEffect_CannonKill_UsesRecordedOperatorOrOwnerWithoutUsingNewOccupant(
        bool hasOwner, bool hasRecordedOperator)
    {
        const uint ordinaryDrop = 10001;
        const uint questDrop = 10002;
        var npc = CreateKraken();
        PrepareDamageAndLoot(npc);
        if (hasOwner || hasRecordedOperator)
            ConfigureCannonDrops(ordinaryDrop, questDrop);
        var gunner = CreatePlayer(npc.ParentWorld, 1);
        var owner = CreatePlayer(npc.ParentWorld, 2);
        var nextGunner = CreatePlayer(npc.ParentWorld, 3);
        npc.ParentWorld.AddObject(owner);
        var gunnerQuest = CreateHuntObjective(gunner, npc.TemplateId);
        var ownerQuest = CreateHuntObjective(owner, npc.TemplateId);
        var nextGunnerQuest = CreateHuntObjective(nextGunner, npc.TemplateId);
        var cannon = new QuietCannon
        {
            ObjId = 4, Hp = 100, ParentWorld = npc.ParentWorld,
            TemplateId = 46, Template = new SlaveTemplate { Id = 46, SlaveKind = SlaveKind.Tank, Mountable = true },
            Summoner = hasOwner ? owner : null,
            OwnerObjId = hasOwner ? owner.ObjId : 0
        };
        // The r208022 Wheeled Mortar casts 15235 from Passenger0. The shot lands after a seat change.
        cannon.AttachedCharacters.Add(AttachPointKind.Passenger0, gunner);
        var skill = new Skill(new SkillTemplate { Id = 15235 })
        {
            VehicleOperator = hasRecordedOperator ? gunner : null
        };
        typeof(Skill).GetProperty(nameof(Skill.OriginalCaster), BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(skill, cannon);
        cannon.AttachedCharacters[AttachPointKind.Passenger0] = nextGunner;
        Unit killerAtDeath = null;
        npc.Events.OnDeath += (_, args) => killerAtDeath = args.Killer;

        DealDamage(cannon, npc, 175, new EffectSource(skill), new SkillCasterMount(cannon.ObjId) { MountSkillTemplateId = 51 });

        var ownerGetsCredit = hasOwner && !hasRecordedOperator;
        uint[] expectedLootOwners = hasRecordedOperator ? [gunner.Id] : ownerGetsCredit ? [owner.Id] : [];
        await Assert.That(npc.Hp).IsEqualTo(0);
        await Assert.That(killerAtDeath).IsSameReferenceAs(cannon);
        await Assert.That(gunnerQuest.Objectives[0]).IsEqualTo(hasRecordedOperator ? 1 : 0);
        await Assert.That(ownerQuest.Objectives[0]).IsEqualTo(ownerGetsCredit ? 1 : 0);
        await Assert.That(nextGunnerQuest.Objectives[0]).IsEqualTo(0);
        await Assert.That(LootRecipients(npc).Select(character => character.Id)).IsEquivalentTo(expectedLootOwners);
        if (expectedLootOwners.Length > 0)
            await Assert.That(npc.LootingContainer.Items.Values.Select(entry => entry.Item.TemplateId)).IsEquivalentTo([ordinaryDrop]);
        await Assert.That(gunner.HonorPoint).IsEqualTo(hasRecordedOperator ? 100 : 0);
        await Assert.That(owner.HonorPoint).IsEqualTo(ownerGetsCredit ? 100 : 0);
        await Assert.That(nextGunner.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task DoDie_TagShareContributor_DoesNotGainTheTagOwnersHonor()
    {
        var npc = CreateKraken();
        var tagger = CreatePlayer(npc.ParentWorld, 1);
        var contributor = CreatePlayer(npc.ParentWorld, 2);
        npc.CharacterTagging.AddTagger(tagger, 90);
        npc.CharacterTagging.AddTagger(contributor, 10);
        var contributorQuestEvents = 0;
        contributor.Events.OnMonsterHunt += (_, _) => contributorQuestEvents++;

        npc.ReduceCurrentHp(contributor, 100);

        await Assert.That(tagger.HonorPoint).IsEqualTo(100);
        await Assert.That(contributor.HonorPoint).IsEqualTo(0);
        await Assert.That(contributorQuestEvents).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DoDie_TaggedPartyOrRaid_EachNearbyMemberGetsTheFullReward(bool party)
    {
        var npc = CreateKraken();
        var owner = CreatePlayer(npc.ParentWorld, 1);
        var near = CreatePlayer(npc.ParentWorld, 2);
        var distant = CreatePlayer(npc.ParentWorld, 3);
        var otherInstance = CreatePlayer(CreateWorld(2), 4);
        var offline = CreatePlayer(npc.ParentWorld, 5);
        distant.Transform.Local.SetPosition(1000, 0, 0);
        SetOnline(offline, false);
        var team = new Team { Id = 1, IsParty = party };
        Character[] members = [owner, near, distant, otherInstance, offline];
        for (var index = 0; index < members.Length; index++)
        {
            team.Members[index] = new TeamMember(members[index]);
            typeof(Character).GetField("<InParty>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(members[index], true);
        }
        _teams[team.Id] = team;
        npc.CharacterTagging.AddTagger(owner, 100);

        npc.ReduceCurrentHp(owner, 100);

        await Assert.That(owner.HonorPoint).IsEqualTo(100);
        await Assert.That(near.HonorPoint).IsEqualTo(100);
        await Assert.That(distant.HonorPoint).IsEqualTo(0);
        await Assert.That(otherInstance.HonorPoint).IsEqualTo(0);
        await Assert.That(offline.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task DoDie_UntaggedPetKill_GrantsHonorToItsOwner()
    {
        var npc = CreateKraken();
        var owner = CreatePlayer(npc.ParentWorld, 1);
        npc.ParentWorld.AddObject(owner);
        var pet = new Mate { ObjId = 9, OwnerObjId = owner.ObjId, ParentWorld = npc.ParentWorld };

        npc.ReduceCurrentHp(pet, 100);

        await Assert.That(owner.HonorPoint).IsEqualTo(100);
    }

    [Test]
    public async Task DoDie_TaggedTeamLeavesRange_DoesNotGiveItsHonorToAnotherKiller()
    {
        var npc = CreateKraken();
        var tagger = CreatePlayer(npc.ParentWorld, 1);
        var killer = CreatePlayer(npc.ParentWorld, 2);
        var team = new Team { Id = 1, IsParty = true };
        team.Members[0] = new TeamMember(tagger);
        _teams[1] = team;
        typeof(Character).GetField("<InParty>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(tagger, true);
        npc.CharacterTagging.AddTagger(tagger, 100);
        tagger.Transform.Local.SetPosition(1000, 0, 0);

        npc.ReduceCurrentHp(killer, 100);

        await Assert.That(tagger.HonorPoint).IsEqualTo(0);
        await Assert.That(killer.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task DoDie_RepeatedAndReentrantCalls_GrantHonorOncePerLife()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        var deaths = 0;
        npc.Events.OnDeath += (_, _) =>
        {
            deaths++;
            npc.DoDie(player, KillReason.Damage);
        };
        for (var life = 0; life < 2; life++)
        {
            npc.Hp = 100;
            npc.CharacterTagging.AddTagger(player, 100);
            npc.ReduceCurrentHp(player, 100);
            npc.DoDie(player, KillReason.Damage);
        }

        await Assert.That(player.HonorPoint).IsEqualTo(200);
        await Assert.That(deaths).IsEqualTo(2);
    }

    [Test]
    public async Task DoDie_ConcurrentCallbacks_GrantHonorOnce()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        npc.CharacterTagging.AddTagger(player, 100);
        npc.Hp = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => npc.DoDie(player, KillReason.Damage))));

        await Assert.That(player.HonorPoint).IsEqualTo(100);
    }

    [Test]
    public async Task DoDie_SoloTaggerLeavesKillRange_DoesNotGainHonor()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        npc.CharacterTagging.AddTagger(player, 100);
        player.Transform.Local.SetPosition(1000, 0, 0);

        npc.ReduceCurrentHp(npc, 100);

        await Assert.That(player.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task GrantKillHonor_FlatAndPercentageBonuses_ApplyBeforeTheServerRate()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        player.AddBonus(1, Bonus(UnitAttribute.HonorPointGainNpcKill, 1));
        player.AddBonus(2, Bonus(UnitAttribute.HonorPointGainNpcKillMul, 100));
        _settings.HonorRate = 2;
        _settings.PvpHonorRate = 7;

        npc.GrantKillHonor(player);

        await Assert.That(player.HonorPoint).IsEqualTo(404);
    }

    [Test]
    [Arguments(0d)]
    [Arguments(-1d)]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    public async Task GrantKillHonor_DisabledOrInvalidRate_DoesNotChangeHonor(double rate)
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        player.HonorPoint = 50;
        _settings.HonorRate = rate;

        npc.GrantKillHonor(player);

        await Assert.That(player.HonorPoint).IsEqualTo(50);
    }

    [Test]
    public async Task GrantKillHonor_ZeroBaseReward_DoesNotCreateHonorFromBonuses()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        npc.Template.HonorPoint = 0;
        player.AddBonus(1, Bonus(UnitAttribute.HonorPointGainNpcKill, 100));

        npc.GrantKillHonor(player);

        await Assert.That(player.HonorPoint).IsEqualTo(0);
    }

    [Test]
    public async Task GrantKillHonor_NearMaximumBalance_DoesNotOverflow()
    {
        var npc = CreateKraken();
        var player = CreatePlayer(npc.ParentWorld, 1);
        player.HonorPoint = int.MaxValue - 1;
        _settings.HonorRate = double.MaxValue;

        npc.GrantKillHonor(player);
        npc.GrantKillHonor(player);

        await Assert.That(player.HonorPoint).IsEqualTo(int.MaxValue);
    }

    private QuietNpc CreateKraken()
    {
        // Reviewed r208022 server compact: npcs.id=7607, honor_point=100, level=50.
        return new QuietNpc
        {
            ObjId = 100, TemplateId = 7607, Level = 50, Hp = 100,
            Template = new NpcTemplate { HonorPoint = 100 }, ParentWorld = CreateWorld(1)
        };
    }

    private WorldInstance CreateWorld(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, ZoneKeys = [1] }, 0, true, id);
        _worlds[id] = world;
        return world;
    }

    private static CharacterMock CreatePlayer(WorldInstance world, uint id)
    {
        var player = new CharacterMock { Id = id, ObjId = id, Level = 1, ParentWorld = world };
        SetOnline(player, true);
        return player;
    }

    private static void SetOnline(Character player, bool online)
    {
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, online);
    }

    private void ReplaceSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _singletons.Add((field, field.GetValue(null)));
        field.SetValue(null, value);
    }

    private static void SetField<T>(T instance, string name, object value)
    {
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    }

    private static Bonus Bonus(UnitAttribute attribute, int value)
    {
        return new Bonus { Template = new BonusTemplate { Attribute = attribute, Value = value }, Value = value };
    }

    private void PrepareDamageAndLoot(Npc npc)
    {
        ReplaceSingleton(new DuelManager());
        var skills = new SkillManager(null, null);
        SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
        ReplaceSingleton(skills);
        var formulas = new FormulaManager();
        ReplaceSingleton(formulas);
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(formulas, new Jace.CalculationEngine());
        SetField(formulas, "_formulas", new Dictionary<uint, Formula> { [20] = new("1") });

        SetField(ItemManager.Instance, "_config", new ItemConfig());
        SetField(ItemManager.Instance, "_lootPackDroppingNpc", new Dictionary<uint, List<LootPackDroppingNpc>>
        {
            [npc.TemplateId] = [new() { LootPackId = 1 }]
        });
        // Use the generated private loot rights without a random drop roll.
        var loot = new LootGameData();
        SetField(loot, "_lootPacks", new Dictionary<uint, LootPack>());
        ReplaceSingleton(loot);
    }

    private static void DealDamage(Unit source, Npc target, int damage, EffectSource effectSource = null, SkillCaster caster = null)
    {
        var effect = new DamageEffect
        {
            DamageType = DamageType.Siege,
            UseFixedDamage = true,
            FixedMin = damage,
            FixedMax = damage
        };
        effect.Apply(source, caster ?? new SkillCasterUnit(source.ObjId), target, new SkillCastUnitTarget(target.ObjId),
            new CastSkill(effectSource?.Skill?.Id ?? 1, 1), effectSource ?? new EffectSource(), null, DateTime.UtcNow);
    }

    private static void ConfigureCannonDrops(uint ordinaryDrop, uint questDrop)
    {
        SetField(ItemManager.Instance, "_templates", new Dictionary<uint, ItemTemplate>
        {
            [ordinaryDrop] = new() { Id = ordinaryDrop },
            [questDrop] = new() { Id = questDrop, LootQuestId = 200 }
        });
        List<Loot> drops =
        [
            new() { ItemId = ordinaryDrop, MinAmount = 1, MaxAmount = 1, AlwaysDrop = true },
            new() { ItemId = questDrop, MinAmount = 1, MaxAmount = 1, AlwaysDrop = true }
        ];
        SetField(LootGameData.Instance, "_lootPacks", new Dictionary<uint, LootPack>
        {
            [1] = new() { Id = 1, Loots = drops, LootsByGroupNo = new() { [0] = drops }, Groups = [], ActabilityGroups = [] }
        });
    }

    private static Quest CreateHuntObjective(Character owner, uint npcId)
    {
        var template = new QuestTemplate { Id = 1 };
        var progress = new QuestComponentTemplate(template) { Id = 2, KindId = QuestComponentKind.Progress };
        progress.ActTemplates.Add(new QuestActObjMonsterHunt(progress)
        {
            ActId = 3, ThisComponentObjectiveIndex = 0, NpcId = npcId, Count = 3
        });
        template.Components.Add(progress.Id, progress);
        var quest = new Quest(template, owner, Mock.Of<IQuestManager>().Object, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Step = QuestComponentKind.Progress
        };
        owner.Quests ??= new CharacterQuests(owner);
        owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }

    private static HashSet<Character> LootRecipients(Npc npc) =>
        (HashSet<Character>)typeof(LootingContainer).GetProperty("EligiblePlayers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(npc.LootingContainer)!;

    private static int RecordedTagDamage(Npc npc, Character player) =>
        ((Dictionary<Character, int>)typeof(Tagging).GetField("_taggers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(npc.CharacterTagging)!).GetValueOrDefault(player);

    private sealed class QuietCannon : Slave
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class QuietNpc : Npc
    {
        public override int MaxHp { get; set; } = 100;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

}
