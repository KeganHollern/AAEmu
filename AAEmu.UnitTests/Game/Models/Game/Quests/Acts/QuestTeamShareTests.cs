using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

[NotInParallel]
public sealed class QuestTeamShareTests
{
    private const float QuestTeamShareRange = 200f;

    private static readonly FieldInfo s_isOnlineField =
        typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_transformInstanceIdField =
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_transformWorldIdField =
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("<WorldId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_teamManagerInstanceField =
        typeof(Singleton<TeamManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_activeTeamsField =
        typeof(TeamManager).GetField("_activeTeams", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private IServiceProvider _previousServiceProvider;
    private ServiceProvider _testServiceProvider;
    private TeamManager _previousTeamManager;
    private TeamManager _teamManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousServiceProvider = SingletonContainer.ServiceProvider;
        _previousTeamManager = (TeamManager)s_teamManagerInstanceField.GetValue(null);

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AppConfiguration>>(Options.Create(new AppConfiguration
        {
            World = new WorldConfig { QuestTeamShareRange = QuestTeamShareRange }
        }));
        _testServiceProvider = services.BuildServiceProvider();
        SingletonContainer.ServiceProvider = _testServiceProvider;

        _teamManager = new TeamManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IChatManager>().Object,
            Mock.Of<ITeamIdManager>().Object);
        s_teamManagerInstanceField.SetValue(null, _teamManager);
    }

    [After(Test)]
    public void TearDown()
    {
        s_teamManagerInstanceField.SetValue(null, _previousTeamManager);
        SingletonContainer.ServiceProvider = _previousServiceProvider;
        _testServiceProvider?.Dispose();
    }

    [Test]
    public async Task WorldConfig_DefaultQuestTeamShareRange_MatchesRetailPartyCreditRange()
    {
        await Assert.That(new WorldConfig().QuestTeamShareRange).IsEqualTo(QuestTeamShareRange);
    }

    [Test]
    public async Task OnTalkMade_TeamShare_CreditsOnlyNearbyEligibleMember()
    {
        const uint npcId = 42;
        var fixture = CreateFixture(
            component => new QuestActObjTalk(component)
            {
                NpcId = npcId,
                TeamShare = true
            },
            rangeOriginX: 100f);
        var npc = CreateVictim(100f);

        var args = new OnTalkMadeArgs
        {
            NpcId = npcId,
            SourcePlayer = fixture.Source,
            Transform = npc.Transform
        };

        fixture.Source.Events.OnTalkMade(fixture.Source, args);
        fixture.Distant.Events.OnTalkMade(fixture.Source, args);
        fixture.Disconnected.Events.OnTalkMade(fixture.Source, args);
        fixture.CrossInstance.Events.OnTalkMade(fixture.Source, args);

        await Assert.That(args.TeamShareAlreadyDistributed).IsTrue();
        await AssertOnlySourceAndNearbyCredited(fixture);
    }

    [Test]
    public async Task OnInteraction_TeamShare_CreditsOnlyNearbyEligibleMember()
    {
        const uint doodadId = 43;
        var fixture = CreateFixture(component => new QuestActObjInteraction(component)
        {
            Count = 5,
            DoodadId = doodadId,
            TeamShare = true
        });

        var args = new OnInteractionArgs
        {
            DoodadId = doodadId,
            SourcePlayer = fixture.Source
        };

        fixture.Source.Events.OnInteraction(fixture.Source, args);
        fixture.Distant.Events.OnInteraction(fixture.Source, args);
        fixture.Disconnected.Events.OnInteraction(fixture.Source, args);
        fixture.CrossInstance.Events.OnInteraction(fixture.Source, args);

        await Assert.That(args.TeamShareAlreadyDistributed).IsTrue();
        await AssertOnlySourceAndNearbyCredited(fixture);
    }

    [Test]
    public async Task OnZoneKill_TeamShare_CreditsOnlyNearbyEligibleMemberOnce()
    {
        var fixture = CreateFixture(
            component => new QuestActObjZoneKill(component)
            {
                CountNpc = 5,
                IsParty = true,
                LvlMinNpc = 1,
                LvlMaxNpc = 55,
                NpcFactionId = FactionsEnum.Monstrosity,
                TeamShare = true
            },
            rangeOriginX: 100f);
        var victim = CreateVictim(100f);

        var args = new OnZoneKillArgs
        {
            Killer = fixture.Source,
            Victim = victim
        };

        fixture.Source.Events.OnZoneKill(fixture.Source, args);
        fixture.Distant.Events.OnZoneKill(fixture.Source, args);
        fixture.Disconnected.Events.OnZoneKill(fixture.Source, args);
        fixture.CrossInstance.Events.OnZoneKill(fixture.Source, args);

        await Assert.That(args.TeamShareAlreadyDistributed).IsTrue();
        await AssertOnlySourceAndNearbyCredited(fixture);
    }

    [Test]
    public async Task OnTalkMade_MultipleMatchingSourceActs_DistributesEventOnce()
    {
        const uint npcId = 42;
        QuestActObjTalk CreateTalkAct(QuestComponentTemplate component) => new(component)
        {
            NpcId = npcId,
            TeamShare = true
        };

        var fixture = CreateFixture(CreateTalkAct);
        var secondSourceQuest = CreateQuest(fixture.Source, CreateTalkAct);
        secondSourceQuest.Step = QuestComponentKind.Progress;
        var nearbyDeliveries = 0;
        fixture.Nearby.Events.OnTalkMade += (_, _) => nearbyDeliveries++;

        fixture.Source.Events.OnTalkMade(fixture.Source, new OnTalkMadeArgs
        {
            NpcId = npcId,
            SourcePlayer = fixture.Source,
            Transform = fixture.Source.Transform
        });

        await Assert.That(nearbyDeliveries).IsEqualTo(1);
        await Assert.That(secondSourceQuest.Objectives[0]).IsEqualTo(1);
        await AssertOnlySourceAndNearbyCredited(fixture);
    }

    [Test]
    public async Task OnZoneKill_PreDistributedTeamCredit_DoesNotFanOutAgain()
    {
        var fixture = CreateFixture(component => new QuestActObjZoneKill(component)
        {
            CountNpc = 5,
            IsParty = true,
            LvlMinNpc = 1,
            LvlMaxNpc = 55,
            NpcFactionId = FactionsEnum.Monstrosity,
            TeamShare = true
        });
        var victim = CreateVictim();
        var sourceDeliveries = 0;
        var nearbyDeliveries = 0;
        fixture.Source.Events.OnZoneKill += (_, _) => sourceDeliveries++;
        fixture.Nearby.Events.OnZoneKill += (_, _) => nearbyDeliveries++;
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);

        questManager.DoOnMonsterHuntEvents(fixture.Source, victim, true);
        questManager.DoOnMonsterHuntEvents(fixture.Nearby, victim, true);

        await Assert.That(sourceDeliveries).IsEqualTo(1);
        await Assert.That(nearbyDeliveries).IsEqualTo(1);
        await AssertOnlySourceAndNearbyCredited(fixture);
    }

    [Test]
    public async Task IsEligibleRecipient_ConfiguredThreeDimensionalRange_IsInclusive()
    {
        AppConfiguration.Instance.World.QuestTeamShareRange = 50f;
        var source = CreateCharacter(1, 101, 0f, true);
        var atLimit = CreateCharacter(2, 102, 50f, true);
        var beyondLimit = CreateCharacter(3, 103, 50.01f, true);
        var crossWorld = CreateCharacter(4, 104, 1f, true, worldId: 2);
        var aboveLimit = CreateCharacter(5, 105, 0f, true, z: 50.01f);
        var disconnected = CreateCharacter(6, 106, 1f, false);
        var crossInstance = CreateCharacter(7, 107, 1f, true, instanceId: 2);

        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(atLimit, source.Transform)).IsTrue();
        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(beyondLimit, source.Transform)).IsFalse();
        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(crossWorld, source.Transform)).IsFalse();
        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(aboveLimit, source.Transform)).IsFalse();
        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(disconnected, source.Transform)).IsFalse();
        await Assert.That(QuestTeamShareEligibility.IsEligibleRecipient(crossInstance, source.Transform)).IsFalse();
    }

    [Test]
    public async Task NpcTeamQuestDeliveries_IncludeOnlyNearbyEligibleMembersAndArePreDistributed()
    {
        var fixture = CreateFixture(component => new QuestActObjInteraction(component)
        {
            Count = 5,
            DoodadId = 43,
            TeamShare = true
        });
        var victim = CreateVictim();

        (Dictionary<Character, bool> Deliveries, int DeliveredCount) GetDeliveries(
            Team team,
            Character personalRecipient)
        {
            var result = new Dictionary<Character, bool>();
            var deliveredCount = victim.DistributeEligibleTeamQuestCredit(
                team,
                personalRecipient,
                (recipient, teamShareAlreadyDistributed) => result.Add(recipient, teamShareAlreadyDistributed));
            return (result, deliveredCount);
        }

        var (deliveries, deliveredCount) = GetDeliveries(fixture.Team, null);
        var recipients = deliveries.Keys;

        await Assert.That(deliveredCount).IsEqualTo(deliveries.Count);
        await Assert.That(recipients.Count).IsEqualTo(2);
        await Assert.That(deliveries.Values.All(teamShareAlreadyDistributed => teamShareAlreadyDistributed)).IsTrue();
        await Assert.That(recipients.Contains(fixture.Source)).IsTrue();
        await Assert.That(recipients.Contains(fixture.Nearby)).IsTrue();
        await Assert.That(recipients.Contains(fixture.Distant)).IsFalse();
        await Assert.That(recipients.Contains(fixture.Disconnected)).IsFalse();
        await Assert.That(recipients.Contains(fixture.CrossInstance)).IsFalse();

        AppConfiguration.Instance.World.QuestTeamShareRange = QuestTeamShareRange + 50f;
        var extendedRangeRecipients = GetDeliveries(fixture.Team, null).Deliveries.Keys;
        await Assert.That(extendedRangeRecipients.Contains(fixture.Distant)).IsTrue();

        AppConfiguration.Instance.World.QuestTeamShareRange = QuestTeamShareRange;
        var recipientsWithDistantKiller = GetDeliveries(fixture.Team, fixture.Distant).Deliveries.Keys;
        await Assert.That(recipientsWithDistantKiller.Contains(fixture.Distant)).IsTrue();

        var outsideTeamKiller = CreateCharacter(6, 106, QuestTeamShareRange + 1f, true);
        var recipientsWithOutsideKiller = GetDeliveries(fixture.Team, outsideTeamKiller).Deliveries.Keys;
        await Assert.That(recipientsWithOutsideKiller.Contains(outsideTeamKiller)).IsFalse();

        var noEligibleTeam = new Team { Id = 11, IsParty = true };
        noEligibleTeam.AddMember(fixture.Disconnected);
        noEligibleTeam.AddMember(fixture.CrossInstance);
        var noEligibleDeliveries = GetDeliveries(noEligibleTeam, null);
        await Assert.That(noEligibleDeliveries.DeliveredCount).IsEqualTo(0);
        await Assert.That(noEligibleDeliveries.Deliveries).IsEmpty();
    }

    private TeamShareFixture CreateFixture<T>(
        Func<QuestComponentTemplate, T> createTemplate,
        float rangeOriginX = 0f)
        where T : QuestActTemplate
    {
        var source = CreateCharacter(1, 101, 0f, true);
        var nearby = CreateCharacter(2, 102, rangeOriginX + QuestTeamShareRange - 1f, true);
        var distant = CreateCharacter(3, 103, rangeOriginX + QuestTeamShareRange + 1f, true);
        var disconnected = CreateCharacter(4, 104, rangeOriginX + 1f, false);
        var crossInstance = CreateCharacter(5, 105, rangeOriginX + 1f, true, instanceId: 2);
        CharacterMock[] members = [source, nearby, distant, disconnected, crossInstance];

        var team = new Team
        {
            Id = 10,
            OwnerId = source.Id,
            IsParty = true
        };
        foreach (var member in members)
            team.AddMember(member);
        GetActiveTeams(_teamManager)[team.Id] = team;

        var quests = new Dictionary<uint, Quest>();
        foreach (var member in members)
        {
            var quest = CreateQuest(member, createTemplate);
            quest.Step = QuestComponentKind.Progress;
            quests.Add(member.Id, quest);
        }

        return new TeamShareFixture(source, nearby, distant, disconnected, crossInstance, team, quests);
    }

    private static Quest CreateQuest<T>(CharacterMock owner, Func<QuestComponentTemplate, T> createTemplate)
        where T : QuestActTemplate
    {
        var questTemplate = new QuestTemplate { Id = 1 };
        var componentTemplate = new QuestComponentTemplate(questTemplate)
        {
            Id = 2,
            KindId = QuestComponentKind.Progress
        };
        var actTemplate = createTemplate(componentTemplate);
        actTemplate.ActId = 3;
        actTemplate.ThisComponentObjectiveIndex = 0;
        componentTemplate.ActTemplates.Add(actTemplate);
        questTemplate.Components.Add(componentTemplate.Id, componentTemplate);

        return new Quest(
            questTemplate,
            owner,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
    }

    private static CharacterMock CreateCharacter(
        uint id,
        uint objId,
        float x,
        bool isOnline,
        uint worldId = 1,
        uint instanceId = 1,
        float z = 0f)
    {
        var character = new CharacterMock
        {
            Id = id,
            ObjId = objId,
            Name = $"Member{id}"
        };
        character.Transform.Local.SetPosition(x, 0f, z);
        s_transformWorldIdField.SetValue(character.Transform, worldId);
        s_transformInstanceIdField.SetValue(character.Transform, instanceId);
        s_isOnlineField.SetValue(character, isOnline);
        return character;
    }

    private static Npc CreateVictim(float x = 0f)
    {
        var victim = new Npc
        {
            Id = 50,
            ObjId = 500,
            Level = 30,
            Faction = new SystemFaction { Id = FactionsEnum.Monstrosity }
        };
        victim.Transform.Local.SetPosition(x, 0f, 0f);
        s_transformWorldIdField.SetValue(victim.Transform, 1u);
        s_transformInstanceIdField.SetValue(victim.Transform, 1u);
        return victim;
    }

    private static async Task AssertOnlySourceAndNearbyCredited(TeamShareFixture fixture)
    {
        await Assert.That(fixture.Quests[fixture.Source.Id].Objectives[0]).IsEqualTo(1);
        await Assert.That(fixture.Quests[fixture.Nearby.Id].Objectives[0]).IsEqualTo(1);
        await Assert.That(fixture.Quests[fixture.Distant.Id].Objectives[0]).IsEqualTo(0);
        await Assert.That(fixture.Quests[fixture.Disconnected.Id].Objectives[0]).IsEqualTo(0);
        await Assert.That(fixture.Quests[fixture.CrossInstance.Id].Objectives[0]).IsEqualTo(0);
    }

    private static ConcurrentDictionary<uint, Team> GetActiveTeams(TeamManager manager)
    {
        return (ConcurrentDictionary<uint, Team>)s_activeTeamsField.GetValue(manager)!;
    }

    private sealed record TeamShareFixture(
        CharacterMock Source,
        CharacterMock Nearby,
        CharacterMock Distant,
        CharacterMock Disconnected,
        CharacterMock CrossInstance,
        Team Team,
        Dictionary<uint, Quest> Quests);
}
