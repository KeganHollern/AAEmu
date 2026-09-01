using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

public sealed class QuestActObjZoneKillTests
{
    [Test]
    [Arguments(5, 0, 5)]
    [Arguments(0, 7, 7)]
    [Arguments(5, 7, 7)]
    public async Task MaxObjective_ActiveQuotas_UsesLargestQuota(int countNpc, int countPlayerKill, int expected)
    {
        var context = CreateContext(countNpc, countPlayerKill);

        await Assert.That(context.Template.Count).IsEqualTo(expected);
        await Assert.That(context.Template.MaxObjective()).IsEqualTo(expected);
    }

    [Test]
    public async Task OnZoneKill_NpcOnlyWithWildcardFilters_CountsAndCapsAtQuota()
    {
        var context = CreateContext(3, 0);
        var pcVictim = CreatePlayerVictim(FactionsEnum.Harani, 30);
        var npcVictim = CreateNpcVictim(FactionsEnum.Monstrosity, 30);

        RecordKill(context, pcVictim);
        for (var i = 0; i < 4; i++)
            RecordKill(context, npcVictim);

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(3);
        await Assert.That(context.Act.RunAct()).IsTrue();
    }

    [Test]
    public async Task OnZoneKill_PlayerOnlyWithWildcardFilters_CountsAndCapsAtQuota()
    {
        var context = CreateContext(0, 3);
        var npcVictim = CreateNpcVictim(FactionsEnum.Monstrosity, 30);
        var pcVictim = CreatePlayerVictim(FactionsEnum.Harani, 30);

        RecordKill(context, npcVictim);
        for (var i = 0; i < 4; i++)
            RecordKill(context, pcVictim);

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(3);
        await Assert.That(context.Act.RunAct()).IsTrue();
    }

    [Test]
    public async Task OnZoneKill_MixedQuotas_CountsNpcAndPlayerVictims()
    {
        var context = CreateContext(3, 3);

        RecordKill(context, CreateNpcVictim(FactionsEnum.Monstrosity, 30));
        RecordKill(context, CreatePlayerVictim(FactionsEnum.Harani, 30));

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(2);
        await Assert.That(context.Act.RunAct()).IsFalse();

        RecordKill(context, CreateNpcVictim(FactionsEnum.Monstrosity, 30));

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(3);
        await Assert.That(context.Act.RunAct()).IsTrue();
    }

    [Test]
    [Arguments(false, FactionsEnum.Monstrosity, 1)]
    [Arguments(false, FactionsEnum.Harani, 0)]
    [Arguments(true, FactionsEnum.Monstrosity, 0)]
    [Arguments(true, FactionsEnum.Harani, 1)]
    public async Task OnZoneKill_PlayerFactionFilter_AppliesIncludeAndExclude(
        bool exclusive,
        FactionsEnum victimFaction,
        int expected)
    {
        var context = CreateContext(0, 2);
        context.Template.PcFactionId = FactionsEnum.Monstrosity;
        context.Template.PcFactionExclusive = exclusive;
        context.Template.LvlMin = 1;
        context.Template.LvlMax = 55;

        RecordKill(context, CreatePlayerVictim(victimFaction, 30));

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 0, 30, 1)]
    [Arguments(20, 0, 30, 1)]
    [Arguments(0, 30, 30, 1)]
    [Arguments(20, 30, 19, 0)]
    [Arguments(20, 30, 31, 0)]
    public async Task OnZoneKill_PlayerLevelFilter_TreatsAbsentBoundsAsWildcards(
        int levelMin,
        int levelMax,
        byte victimLevel,
        int expected)
    {
        var context = CreateContext(0, 2);
        context.Template.PcFactionId = FactionsEnum.Monstrosity;
        context.Template.LvlMin = levelMin;
        context.Template.LvlMax = levelMax;

        RecordKill(context, CreatePlayerVictim(FactionsEnum.Monstrosity, victimLevel));

        await Assert.That(context.Quest.Objectives[0]).IsEqualTo(expected);
    }

    private static ZoneKillContext CreateContext(int countNpc, int countPlayerKill)
    {
        var questTemplate = new QuestTemplate { Id = 1 };
        var componentTemplate = new QuestComponentTemplate(questTemplate)
        {
            Id = 2,
            KindId = QuestComponentKind.Progress
        };
        var actTemplate = new QuestActObjZoneKill(componentTemplate)
        {
            ActId = 3,
            CountNpc = countNpc,
            CountPlayerKill = countPlayerKill,
            ThisComponentObjectiveIndex = 0
        };
        componentTemplate.ActTemplates.Add(actTemplate);
        questTemplate.Components.Add(componentTemplate.Id, componentTemplate);

        var owner = new CharacterMock
        {
            Id = 10,
            ObjId = 100,
            Name = "Questor"
        };
        var quest = new Quest(
            questTemplate,
            owner,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);
        var act = quest.QuestSteps[QuestComponentKind.Progress]
            .Components[componentTemplate.Id]
            .Acts.Single();

        return new ZoneKillContext(quest, actTemplate, act, owner);
    }

    private static Npc CreateNpcVictim(FactionsEnum factionId, byte level)
    {
        return new Npc
        {
            Id = 20,
            ObjId = 200,
            Level = level,
            Faction = new SystemFaction { Id = factionId }
        };
    }

    private static CharacterMock CreatePlayerVictim(FactionsEnum factionId, byte level)
    {
        return new CharacterMock
        {
            Id = 20,
            ObjId = 200,
            Level = level,
            Faction = new SystemFaction { Id = factionId }
        };
    }

    private static void RecordKill(ZoneKillContext context, Unit victim)
    {
        context.Act.OnZoneKill(context.Owner, new OnZoneKillArgs
        {
            Killer = context.Owner,
            Victim = victim
        });
    }

    private sealed record ZoneKillContext(
        Quest Quest,
        QuestActObjZoneKill Template,
        QuestAct Act,
        CharacterMock Owner);
}
