using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

public sealed partial class QuestFollowUnitTests
{
    [Test]
    [Arguments(3656u)]
    [Arguments(1313u)]
    public async Task EscortPath_OnlyTheClaimedGuardAtTheDestinationCompletesTheReturn(uint id)
    {
        var satish = id == 3656;
        var template = BuildEscortTemplate(satish);
        Register(template);
        var actorId = satish ? 9846u : 3138u;
        var name = satish ? "AIPath_141_clay02_580" : "q1313_anci";
        var path = AiPathsManager.Instance.LoadAiPathPoints(name);
        await Assert.That(path.Count).IsGreaterThan(2);
        var start = path[0].Position;
        var end = path[^1].Position;
        _world.Template.Name = "main_world";
        _world.Template.CellX = (int)start.X / WorldManager.CELL_SIZE + 1;
        _world.Template.CellY = (int)start.Y / WorldManager.CELL_SIZE + 1;
        _world.Template.ZoneKeyByRegions = new uint[_world.Template.CellX * WorldManager.SECTORS_PER_CELL,
            _world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        var guard = CreateNpc(91, actorId);
        var duplicate = CreateNpc(92, actorId);
        var reporter = CreateNpc(93, satish ? 2979u : 3137u);
        // Both natural spawns use their measured runtime terrain height.
        guard.Transform.Local.Position = guard.Ai.IdlePosition = guard.Ai.HomePosition =
            new Vector3(start.X, start.Y, satish ? 129.486f : 257.517f);
        duplicate.Transform.Local.Position = reporter.Transform.Local.Position = end;
        _owner.Region.AddObject(_owner);
        _owner.Region.AddObject(reporter);
        _owner.Region.AddObject(guard);
        _owner.Region.AddObject(duplicate);
        var giver = satish ? reporter : guard;
        _owner.Transform.Local.Position = giver.Transform.World.Position;
        await Assert.That(_owner.Quests.AddQuestFromNpc(id, giver.ObjId)).IsTrue();
        var quest = _owner.Quests.ActiveQuests[id];
        _quests.Add(quest);
        _manager.DoQueuedEvaluations();
        var sphere = new SphereQuest
        {
            QuestId = id, ComponentId = satish ? 15274u : 6548u,
            Xyz = satish ? new Vector3(17019, 7742, 136.7f) : new Vector3(22337.443f, 12154.51f, 266.515f),
            Radius = 10f
        };
        var trigger = new SphereQuestTrigger { Quest = quest, Owner = _owner, Sphere = sphere, NpcTemplate = actorId, SphereId = satish ? 1266u : 461u };
        _world.SphereQuestManager.AddSphereQuestTrigger(trigger);
        var objective = satish ? 1 : 0;

        _owner.Transform.Local.Position = start;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(quest.Objectives[objective]).IsEqualTo(0);
        if (satish)
        {
            await Assert.That(guard.Ai.AiFollowUnitObj).IsNull();
            Talk(quest, guard);
            _manager.DoQueuedEvaluations();
        }
        await Assert.That(guard.Ai.IsQuestFollowOwner(quest)).IsTrue();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        _owner.Transform.Local.Position = end;
        trigger.Tick(TimeSpan.Zero);
        await Assert.That(quest.Objectives[objective]).IsEqualTo(0);
        _owner.Transform.Local.Position = start;
        for (var i = 0; i < 650; i++)
        {
            guard.Ai.Tick(TimeSpan.FromMilliseconds(100));
            trigger.Tick(TimeSpan.Zero);
        }
        await Assert.That(Vector3.Distance(guard.Transform.World.Position, end)).IsLessThan(1.1f);
        await Assert.That(quest.Objectives[objective]).IsEqualTo(0);
        await Assert.That(guard.Movements.Count).IsGreaterThan(10);
        await Assert.That(guard.Movements[^1].Flags.HasFlag(MoveTypeFlags.Stopping)).IsTrue();
        _owner.Transform.Local.Position = end;
        trigger.Tick(TimeSpan.Zero);
        _manager.DoQueuedEvaluations();
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(quest.Objectives[objective]).IsEqualTo(1);
        await Assert.That(guard.Ai.IsQuestFollowOwner(quest)).IsTrue();
        _manager.DoReportEvents(_owner, id, reporter.ObjId, 0, 0);
        _manager.DoQueuedEvaluations();
        await Assert.That(guard.Ai.IsQuestFollowOwner(quest)).IsFalse();
        TickEscort(guard, 650);
        await Assert.That(Vector3.Distance(guard.Transform.World.Position, start)).IsLessThan(1.1f);
        await Assert.That(guard.Despawned).IsFalse();
        var next = CreateOwner(8, 80);
        _owner.Region.AddObject(next);
        next.Transform.Local.Position = giver.Transform.World.Position;
        await Assert.That(next.Quests.AddQuestFromNpc(id, giver.ObjId)).IsTrue();
        var newQuest = next.Quests.ActiveQuests[id];
        _quests.Add(newQuest);
        if (satish)
        {
            next.Transform.Local.Position = guard.Transform.World.Position;
            Talk(newQuest, guard);
        }
        _manager.DoQueuedEvaluations();
        await Assert.That(guard.Ai.IsQuestFollowOwner(newQuest)).IsTrue();
    }

    [Test]
    [Arguments(3656u, false)]
    [Arguments(3656u, true)]
    [Arguments(1313u, false)]
    [Arguments(1313u, true)]
    public async Task EscortPath_BusyOriginalReturnsAfterDropOrDisconnectAndServesTheNextPlayer(uint id, bool disconnect)
    {
        var satish = id == 3656;
        Register(BuildEscortTemplate(satish));
        _world.Template.Name = "main_world";
        var path = AiPathsManager.Instance.LoadAiPathPoints(satish ? "AIPath_141_clay02_580" : "q1313_anci");
        var start = path[0].Position;
        var guard = CreateNpc(91, satish ? 9846u : 3138u);
        guard.Transform.Local.Position = guard.Ai.IdlePosition = guard.Ai.HomePosition = start;
        var giver = satish ? CreateNpc(92, 2979) : guard;
        giver.Transform.Local.Position = start;
        var next = CreateOwner(8, 80);
        _owner.Transform.Local.Position = next.Transform.Local.Position = start;
        await Assert.That(_owner.Quests.AddQuestFromNpc(id, giver.ObjId)).IsTrue();
        var first = _owner.Quests.ActiveQuests[id];
        _quests.Add(first);
        if (satish)
            Talk(first, guard);
        _manager.DoQueuedEvaluations();

        // Satish's play-through starts at a separate giver. Caliope is the shared giver herself.
        var accepted = next.Quests.AddQuestFromNpc(id, giver.ObjId);
        await Assert.That(accepted).IsEqualTo(satish);
        if (!satish)
        {
            await Assert.That(_manager.QuestTimeoutTask[_owner.Id].ContainsKey(id)).IsTrue();
            await Assert.That(_manager.QuestTimeoutTask.ContainsKey(next.Id)).IsFalse();
        }
        if (satish)
        {
            var pending = next.Quests.ActiveQuests[id];
            _quests.Add(pending);
            Talk(pending, guard);
            await Assert.That(pending.Objectives[0]).IsEqualTo(0);
        }
        await Assert.That(guard.Ai.IsQuestFollowOwner(first)).IsTrue();
        TickEscort(guard, 120);
        await Assert.That(Vector3.Distance(guard.Transform.World.Position, start)).IsGreaterThan(5f);

        if (disconnect)
        {
            _owner.Quests.PrepareEscortQuestsForDisconnect();
            _owner.Events.OnDisconnect(_owner, new OnDisconnectArgs { Player = _owner });
        }
        else
            _owner.Quests.DropQuest(id, true);

        await Assert.That(guard.Ai.IsQuestFollowOwner(first)).IsFalse();
        TickEscort(guard, 220);
        var returned = guard.Transform.World.Position;
        await Assert.That(Vector3.Distance(returned, start)).IsLessThan(1.1f);
        next.Transform.Local.Position = returned;
        if (satish)
            Talk(next.Quests.ActiveQuests[id], guard);
        else
        {
            await Assert.That(next.Quests.AddQuestFromNpc(id, guard.ObjId)).IsTrue();
            _quests.Add(next.Quests.ActiveQuests[id]);
        }
        _manager.DoQueuedEvaluations();
        TickEscort(guard, 35);
        await Assert.That(guard.Ai.IsQuestFollowOwner(next.Quests.ActiveQuests[id])).IsTrue();
        await Assert.That(Vector3.Distance(guard.Transform.World.Position, returned)).IsGreaterThan(1f);
    }

    [Test]
    public async Task EscortPath_ASecondCaliopeOnAnotherFloorCannotStartTheWalk()
    {
        Register(BuildEscortTemplate(false));
        _world.Template.Name = "main_world";
        var guard = CreateNpc(91, 3138);
        var start = AiPathsManager.Instance.LoadAiPathPoints("q1313_anci")[0].Position;
        guard.Ai.HomePosition = start + new Vector3(0, 0, 8);
        _owner.Transform.Local.Position = guard.Transform.Local.Position = guard.Ai.IdlePosition = start;
        await Assert.That(_owner.Quests.AddQuestFromNpc(1313, guard.ObjId)).IsFalse();
        await Assert.That(_owner.Quests.ActiveQuests.ContainsKey(1313)).IsFalse();
        await Assert.That(guard.Ai.AiFollowUnitObj).IsNull();
    }

    private static void TickEscort(ProbeNpc guard, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            guard.Ai.Tick(TimeSpan.FromMilliseconds(100));
    }

    private static QuestTemplate BuildEscortTemplate(bool satish)
    {
        var template = new QuestTemplate { Id = satish ? 3656u : 1313u };
        QuestComponentTemplate Component(uint id, QuestComponentKind kind)
        {
            var component = new QuestComponentTemplate(template) { Id = id, KindId = kind };
            template.Components.Add(id, component);
            return component;
        }

        var start = Component(satish ? 15272u : 6547u, QuestComponentKind.Start);
        var guardId = satish ? 9846u : 3138u;
        start.ActTemplates.Add(new QuestActConAcceptNpc(start) { ActId = satish ? 20949u : 9273u, NpcId = satish ? 2979u : guardId });
        var walk = satish ? Component(15273, QuestComponentKind.Progress) : start;
        walk.NpcAiId = QuestNpcAiName.FollowPath;
        walk.NpcId = guardId;
        walk.AiPathTypeId = satish ? PathType.Remove : PathType.Idle;
        walk.AiPathName = satish ? "AIPath_141_clay02_580" : "q1313_anci";
        if (satish)
            walk.ActTemplates.Add(new QuestActObjTalk(walk) { ActId = 20950, NpcId = guardId, Count = 1, ThisComponentObjectiveIndex = 0 });
        walk.ActTemplates.Add(new QuestActCheckGuard(walk) { ActId = satish ? 20951u : 12289u, NpcId = guardId });
        if (!satish)
            start.ActTemplates.Add(new QuestActCheckTimer(start) { ActId = 12290, LimitTime = 180000 });
        var arrival = Component(satish ? 15274u : 6548u, QuestComponentKind.Progress);
        if (!satish)
        {
            arrival.NpcAiId = QuestNpcAiName.RunCommandSet;
            arrival.NpcId = guardId;
            arrival.AiCommandSetId = 8;
        }
        arrival.ActTemplates.Add(new QuestActObjSphere(arrival)
        {
            ActId = satish ? 21290u : 9274u, SphereId = satish ? 1266u : 461u,
            NpcId = guardId, Count = 1, ThisComponentObjectiveIndex = (byte)(satish ? 1 : 0)
        });
        var ready = Component(satish ? 15276u : 6549u, QuestComponentKind.Ready);
        ready.ActTemplates.Add(new QuestActConReportNpc(ready) { ActId = satish ? 20952u : 9275u, NpcId = satish ? 2979u : 3137u });
        Component(satish ? 15277u : 6550u, QuestComponentKind.Reward);
        return template;
    }
}
