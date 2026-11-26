using System.Numerics;
using System.Reflection;

using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

public sealed partial class QuestFollowUnitTests
{
    [Test]
    [Arguments(2f)]
    [Arguments(18f)]
    public async Task CombatQuest_FirstHitStartsTheAuthoredQuestOnce(float distance)
    {
        const uint questId = 2432;
        var gladosh = CreateNpc(81, 7914);
        gladosh.Template.EngageCombatGiveQuestId = questId;
        gladosh.Transform.Local.Position = _owner.Transform.Local.Position + new Vector3(distance, 0, 0);
        RegisterButcherBasher();
        gladosh.OnDamageReceived(_owner, 1);
        await Assert.That(_owner.Quests.HasQuest(questId)).IsTrue();
        var quest = _owner.Quests.ActiveQuests[questId];
        _quests.Add(quest);
        _manager.DoQueuedEvaluations();
        gladosh.OnDamageReceived(_owner, 1);

        await Assert.That(quest.AcceptorId).IsEqualTo(gladosh.TemplateId);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(_owner.Quests.ActiveQuests[questId]).IsSameReferenceAs(quest);
        await Assert.That(_saved.Count(q => ReferenceEquals(q, quest))).IsEqualTo(1);
    }

    [Test]
    [Arguments("talk")]
    [Arguments("hidden")]
    [Arguments("no_quest")]
    public async Task CombatQuest_OrdinaryTalkAndUnrelatedCombatDoNotStartAtRange(string action)
    {
        var template = RegisterButcherBasher();
        var gladosh = CreateNpc(81, 7914);
        gladosh.Template.EngageCombatGiveQuestId = action == "no_quest" ? 0u : template.Id;
        gladosh.Transform.Local.Position = _owner.Transform.Local.Position + new Vector3(18f, 0, 0);
        if (action == "talk")
            await Assert.That(_owner.Quests.AddQuestFromNpc(template.Id, gladosh.ObjId)).IsFalse();
        else
        {
            gladosh.IsVisible = action != "hidden";
            gladosh.OnDamageReceived(_owner, 1);
        }
        await Assert.That(_owner.Quests.HasQuest(template.Id)).IsFalse();
        await Assert.That(_saved).IsEmpty();
    }

    [Test]
    [Arguments(3, 149)]
    [Arguments(4, 148)]
    public async Task CombatQuest_FirstHitRespectsLevelAndFaction(int level, int motherFaction)
    {
        var template = RegisterButcherBasher();
        _owner.Level = (byte)level;
        _owner.Faction = new SystemFaction { MotherId = (FactionsEnum)motherFaction };
        var gladosh = CreateNpc(81, 7914);
        gladosh.Template.EngageCombatGiveQuestId = template.Id;
        gladosh.Transform.Local.Position = _owner.Transform.Local.Position + new Vector3(18f, 0, 0);
        gladosh.OnDamageReceived(_owner, 1);
        await Assert.That(_owner.Quests.HasQuest(template.Id)).IsFalse();
        await Assert.That(_saved).IsEmpty();
    }

    [Test]
    public async Task CombatQuest_DragonfireElementalDoesNotReplaceItsSphereStartAtRange()
    {
        const uint questId = 4392;
        var template = new QuestTemplate { Id = questId };
        var start = new QuestComponentTemplate(template) { Id = 19100, KindId = QuestComponentKind.Start };
        start.ActTemplates.Add(new QuestActConAcceptSphere(start) { ActId = 30676, DetailId = 652, SphereId = 1526 });
        template.Components.Add(start.Id, start);
        Register(template);
        var previousTarget = CreateNpc(80, 2000);
        _owner.CurrentTarget = previousTarget;
        var elemental = CreateNpc(81, 2003);
        elemental.Template.EngageCombatGiveQuestId = questId;
        elemental.Transform.Local.Position = _owner.Transform.Local.Position + new Vector3(18f, 0, 0);

        elemental.OnDamageReceived(_owner, 1);

        await Assert.That(_owner.Quests.HasQuest(questId)).IsFalse();
        await Assert.That(_owner.CurrentTarget).IsSameReferenceAs(previousTarget);
        await Assert.That(_saved).IsEmpty();
    }

    private QuestTemplate RegisterButcherBasher()
    {
        const uint questId = 2432;
        var template = new QuestTemplate { Id = questId };
        var start = new QuestComponentTemplate(template) { Id = 10475, KindId = QuestComponentKind.Start };
        start.ActTemplates.Add(new QuestActConAcceptComponent(start) { ActId = 28098, QuestContextId = questId });
        template.Components.Add(start.Id, start);
        var objective = new QuestComponentTemplate(template) { Id = 20523, KindId = QuestComponentKind.Progress };
        objective.ActTemplates.Add(new QuestActObjAggro(objective)
        {
            ActId = 28102, Range = 30, Rank1 = 10, Rank1Ratio = 100, Rank2 = 20, Rank2Ratio = 50,
            Rank3 = 30, Rank3Ratio = 30, UseAlias = true, QuestActObjAliasId = 1965
        });
        template.Components.Add(objective.Id, objective);
        var reward = new QuestComponentTemplate(template) { Id = 10478, KindId = QuestComponentKind.Reward };
        reward.ActTemplates.Add(new QuestActConAutoComplete(reward) { ActId = 28105 });
        template.Components.Add(reward.Id, reward);
        Register(template);
        _owner.Level = 4;
        _owner.Faction = new SystemFaction { MotherId = (FactionsEnum)149 };
        var rows = (Dictionary<string, List<UnitReqs>>)typeof(UnitRequirementsGameData)
            .GetProperty("_unitReqsByOwnerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(UnitRequirementsGameData.Instance)!;
        rows["QuestComponent"] =
        [
            new() { Id = 35866, OwnerId = start.Id, OwnerType = "QuestComponent", KindType = UnitReqsKindType.Level, Value1 = 4 },
            new() { Id = 35867, OwnerId = start.Id, OwnerType = "QuestComponent", KindType = UnitReqsKindType.MotherFaction, Value1 = 149 }
        ];
        return template;
    }
}
