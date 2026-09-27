using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
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

    private sealed class QuietNpc : Npc
    {
        public override int MaxHp { get; set; } = 100;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

}
