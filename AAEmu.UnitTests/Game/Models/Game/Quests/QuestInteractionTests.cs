using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests;

[NotInParallel]
public sealed class QuestInteractionTests
{
    private const string DefaultDoodadModel = "cgf://Game/Objects/Env/01_nuia/001_ndeco/quest/ndeco_quest_case02.cgf";
    private QuestInteractionTestModels _models;
    private WorldInstance _world;
    private CharacterMock _owner;
    private Npc _npc;
    private Doodad _doodad;
    private QuestManager _manager;

    [Before(Test)]
    public void SetUp()
    {
        _models = new QuestInteractionTestModels();
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        _owner = new CharacterMock { Id = 7, ObjId = 70, ModelId = 1 };
        _owner.Quests = new CharacterQuests(_owner);
        _owner.Region = QuestInteractionTestModels.CreateRegion(_world);
        SetParentWorld(_owner, _world);
        _npc = new Npc { ObjId = 81, TemplateId = 810, ModelId = 1, Template = new NpcTemplate { Scale = 1 } };
        _doodad = new Doodad { ObjId = 82, TemplateId = 685, Template = new DoodadTemplate { Model = DefaultDoodadModel } };
        foreach (var source in new BaseUnit[] { _npc, _doodad })
        {
            SetParentWorld(source, _world);
            source.Region = _owner.Region;
            source.IsVisible = true;
            _world.AddObject(source);
        }
        _manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
    }

    [After(Test)]
    public void TearDown() => _models.Dispose();

    [Test]
    [Arguments(3.999f, true)]
    [Arguments(4f, false)]
    [Arguments(4.001f, false)]
    public async Task NpcRange_UsesStrictThreeMeterSphereEdgeDistance(float x, bool expected)
    {
        _npc.Transform.Local.Position = new Vector3(x, 0, 0);
        await Assert.That(QuestInteraction.CanInteractWithNpc(_owner, _npc)).IsEqualTo(expected);
    }

    [Test]
    public async Task NpcRange_IncludesVerticalDistance()
    {
        _npc.Transform.Local.Position = new Vector3(0, 0, 4);
        await Assert.That(QuestInteraction.CanInteractWithNpc(_owner, _npc)).IsFalse();
    }

    [Test]
    public async Task NpcRange_UsesBothModelHeightsAndScaledRadius()
    {
        var ownerModel = new ActorModel { Height = 1, Radius = 0.5f };
        var npcModel = new ActorModel { Height = 2, Radius = 1 };
        _npc.Template.Scale = 2;
        _npc.Transform.Local.Position = new Vector3(5.499f, 0, -3);
        await Assert.That(QuestInteraction.IsWithinNpcRange(_owner, ownerModel, _npc, npcModel)).IsTrue();
        _npc.Transform.Local.Position = new Vector3(5.5f, 0, -3);
        await Assert.That(QuestInteraction.IsWithinNpcRange(_owner, ownerModel, _npc, npcModel)).IsFalse();
    }

    [Test]
    public async Task NpcRange_RotatesTheLocalSphereCenter()
    {
        var model = new ActorModel { Height = 2, Radius = 0 };
        _npc.Transform.Local.Rotation = new Vector3(MathF.PI, 0, 0);
        await Assert.That(QuestInteraction.IsWithinNpcRange(_owner, model, _npc, model)).IsFalse();
    }

    [Test]
    public async Task NpcRange_RejectsUnknownModelAndInvalidPosition()
    {
        var model = new ActorModel { Height = 1, Radius = 0.5f };
        await Assert.That(QuestInteraction.IsWithinNpcRange(_owner, model, _npc, null)).IsFalse();
        _npc.Transform.Local.Position = new Vector3(float.NaN, 0, 0);
        await Assert.That(QuestInteraction.CanInteractWithNpc(_owner, _npc)).IsFalse();
    }

    [Test]
    [Arguments("hidden")]
    [Arguments("unstreamed")]
    [Arguments("other_instance")]
    [Arguments("other_world")]
    [Arguments("stale_instance")]
    public async Task AllSourceEvents_RejectUnavailableObjectsWithoutMutation(string state)
    {
        MakeUnavailable(_npc, state);
        MakeUnavailable(_doodad, state);
        var target = new Npc { ObjId = 99 };
        _owner.CurrentTarget = target;
        var reportNpc = 0;
        var reportDoodad = 0;
        var talk = 0;
        var talkGroup = 0;
        var express = 0;
        _owner.Events.OnReportNpc += (_, _) => reportNpc++;
        _owner.Events.OnReportDoodad += (_, _) => reportDoodad++;
        _owner.Events.OnTalkMade += (_, _) => talk++;
        _owner.Events.OnTalkNpcGroupMade += (_, _) => talkGroup++;
        _owner.Events.OnExpressFire += (_, _) => express++;
        var quest = AddActiveQuest();

        await Assert.That(_owner.Quests.AddQuestFromNpc(12, _npc.ObjId)).IsFalse();
        await Assert.That(_owner.Quests.AddQuestFromDoodad(12, _doodad.ObjId)).IsFalse();
        _manager.DoReportEvents(_owner, 12, _npc.ObjId, 0, 0);
        _manager.DoReportEvents(_owner, 12, 0, _doodad.ObjId, 0);
        _manager.DoTalkMadeEvents(_owner, _owner, _npc.ObjId, 12, 0, 0);
        _manager.DoOnExpressFireEvents(_owner, 1, _owner.ObjId, _npc.ObjId);

        await Assert.That(reportNpc + reportDoodad + talk + talkGroup + express).IsEqualTo(0);
        await Assert.That(_owner.CurrentTarget).IsSameReferenceAs(target);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
        await Assert.That(quest.SelectedRewardIndex).IsEqualTo(0);
    }

    [Test]
    public async Task RemoteNpc_RejectsAcceptReportAndTalkWithoutEvents()
    {
        _npc.Transform.Local.Position = new Vector3(1_000, 0, 0);
        var count = 0;
        _owner.Events.OnReportNpc += (_, _) => count++;
        _owner.Events.OnTalkMade += (_, _) => count++;
        AddActiveQuest();

        await Assert.That(_owner.Quests.AddQuestFromNpc(12, _npc.ObjId)).IsFalse();
        _manager.DoReportEvents(_owner, 12, _npc.ObjId, 0, 0);
        _manager.DoTalkMadeEvents(_owner, _owner, _npc.ObjId, 12, 0, 0);
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(_owner.CurrentTarget).IsNull();
    }

    [Test]
    public async Task VisibleNearbySources_DeliverEvents()
    {
        var count = 0;
        _owner.Events.OnReportNpc += (_, _) => count++;
        _owner.Events.OnReportDoodad += (_, _) => count++;
        _owner.Events.OnTalkMade += (_, _) => count++;
        _owner.Events.OnExpressFire += (_, _) => count++;
        _owner.CurrentTarget = _npc;
        AddActiveQuest();
        _manager.DoReportEvents(_owner, 12, _npc.ObjId, 0, 0);
        _manager.DoReportEvents(_owner, 12, 0, _doodad.ObjId, 0);
        _manager.DoTalkMadeEvents(_owner, _owner, _npc.ObjId, 12, 0, 0);
        _manager.DoOnExpressFireEvents(_owner, 1, _owner.ObjId, _npc.ObjId);
        await Assert.That(count).IsEqualTo(4);
    }

    [Test]
    [Arguments(9.999f, true)]
    [Arguments(10f, false)]
    public async Task DoodadRange_UsesStrictNineMeterSphereEdgeDistance(float x, bool expected)
    {
        _doodad.Transform.Local.Position = new Vector3(x, 0, 1);
        await Assert.That(QuestInteraction.CanInteractWithDoodad(_owner, _doodad)).IsEqualTo(expected);
    }

    [Test]
    public async Task DoodadRange_RejectsRemoteAcceptAndReport()
    {
        _doodad.Transform.Local.Position = new Vector3(1_000, 0, 0);
        var count = 0;
        _owner.Events.OnReportDoodad += (_, _) => count++;
        AddActiveQuest();
        await Assert.That(_owner.Quests.AddQuestFromDoodad(12, _doodad.ObjId)).IsFalse();
        _manager.DoReportEvents(_owner, 12, 0, _doodad.ObjId, 0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task DoodadRange_UsesNearestAuthoredSphereAndModelScale()
    {
        _doodad.SetScale(2);
        _doodad.Transform.Local.Position = new Vector3(12, 0, -3);
        var shapes = new[]
        {
            new QuestInteractionSphere(new Vector3(0, 0, 2), 0.5f),
            new QuestInteractionSphere(new Vector3(-1, 0, 2), 1)
        };
        await Assert.That(QuestInteraction.IsWithinDoodadRange(_owner,
            new ActorModel { Height = 1, Radius = 0.5f }, _doodad, shapes)).IsTrue();
        _doodad.Transform.Local.Position = new Vector3(30, 0, -3);
        await Assert.That(QuestInteraction.IsWithinDoodadRange(_owner,
            new ActorModel { Height = 1, Radius = 0.5f }, _doodad, shapes)).IsFalse();
    }

    [Test]
    public async Task DoodadCatalog_UsesPhaseOverrideAndRejectsUnknownModels()
    {
        await Assert.That(QuestDoodadInteractionShapes.Get(_doodad)).IsNotNull();
        _doodad.Template.FuncGroups.Add(new DoodadFuncGroups { Id = 0, Model = "cgf://unknown.cgf" });
        await Assert.That(QuestDoodadInteractionShapes.Get(_doodad)).IsNull();
        await Assert.That(QuestInteraction.CanInteractWithDoodad(_owner, _doodad)).IsFalse();
    }

    [Test]
    public async Task DoodadCatalog_UsesKnownPhaseAndEmptyOverrideFallback()
    {
        var fallback = QuestDoodadInteractionShapes.Get(_doodad);
        var phase = new DoodadFuncGroups
        {
            Id = 0,
            Model = "cgf://game/objects/env/02_harihara/001_hdeco/quest/hdeco_quest_an_mausoleum_gate_close.cgf"
        };
        _doodad.Template.FuncGroups.Add(phase);
        var authored = QuestDoodadInteractionShapes.Get(_doodad);
        await Assert.That(authored.Count).IsEqualTo(1);
        await Assert.That(authored[0].Radius).IsEqualTo(2.6463327f);
        await Assert.That(authored[0].Center.Z).IsEqualTo(3.1563427f);
        phase.Model = "";
        await Assert.That(QuestDoodadInteractionShapes.Get(_doodad)).IsSameReferenceAs(fallback);
    }

    [Test]
    public async Task DoodadCatalog_NormalizesCaseSeparatorsAndGamePrefix()
    {
        var expected = QuestDoodadInteractionShapes.Get(_doodad);
        _doodad.Template.Model = "CGF://OBJECTS\\ENV\\01_NUIA\\001_NDECO\\QUEST\\NDECO_QUEST_CASE02.CGF/";
        await Assert.That(QuestDoodadInteractionShapes.Get(_doodad)).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task DoodadCatalog_UsesEntityCgaHelperWithParentTransforms()
    {
        _doodad.Template.FuncGroups.Add(new DoodadFuncGroups
        {
            Id = 0,
            Model = "prefab://prefabs/quest_prop.xml/quest_prop.quest_case"
        });
        var spheres = QuestDoodadInteractionShapes.Get(_doodad);
        await Assert.That(spheres.Count).IsEqualTo(1);
        await Assert.That(spheres[0].Center).IsEqualTo(new Vector3(-0.0018835446f, -0.21665555f, 0.23487173f));
        await Assert.That(spheres[0].Radius).IsEqualTo(0.41113842f);
        _doodad.Transform.Local.Position = new Vector3(9.91f, 0, 1) - spheres[0].Center;
        await Assert.That(QuestInteraction.CanInteractWithDoodad(_owner, _doodad)).IsTrue();
        _doodad.Transform.Local.Position = new Vector3(9.92f, 0, 1) - spheres[0].Center;
        await Assert.That(QuestInteraction.CanInteractWithDoodad(_owner, _doodad)).IsFalse();
    }

    [Test]
    [Arguments("hidden")]
    [Arguments("unstreamed")]
    [Arguments("other_instance")]
    public async Task Emote_RejectsUnavailableSelectedTarget(string state)
    {
        _owner.CurrentTarget = _npc;
        MakeUnavailable(_npc, state);
        var count = 0;
        _owner.Events.OnExpressFire += (_, _) => count++;
        _manager.DoOnExpressFireEvents(_owner, 1, _owner.ObjId, _npc.ObjId);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task Emote_RejectsAVisibleNpcOtherThanTheSelectedTarget()
    {
        var count = 0;
        _owner.Events.OnExpressFire += (_, _) => count++;
        _manager.DoOnExpressFireEvents(_owner, 1, _owner.ObjId, _npc.ObjId);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0U, 0U, 1U)]
    [Arguments(81U, 0U, 1U)]
    [Arguments(0U, 82U, 1U)]
    [Arguments(81U, 82U, 0U)]
    public async Task StartPacket_RejectsSphereSpoofAndAmbiguousSources(uint npc, uint doodad, uint sphere)
    {
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _owner };
        var stream = new PacketStream();
        stream.Write(12U);
        stream.WriteBc(npc);
        stream.WriteBc(doodad);
        stream.Write(sphere);
        new CSStartQuestContextPacket { Connection = connection }.Read(new PacketStream(stream.GetBytes()));
        await Assert.That(_owner.Quests.ActiveQuests).IsEmpty();
        await Assert.That(_owner.CurrentTarget).IsNull();
    }

    [Test]
    [Arguments("npc", 0U)]
    [Arguments("doodad", 0U)]
    [Arguments("sphere", 0U)]
    [Arguments("sphere", 81U)]
    public async Task StartPacket_RejectsMissingOrWrongWorldSourceBeforeStateChanges(string kind, uint npc)
    {
        var template = CreateStartTemplate(kind);
        var templates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;
        templates.Add(template.Id, template);
        var field = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, _manager);
            var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _owner };
            var stream = new PacketStream();
            stream.Write(template.Id);
            stream.WriteBc(npc);
            stream.WriteBc(0U);
            stream.Write(0U);
            new CSStartQuestContextPacket { Connection = connection }.Read(new PacketStream(stream.GetBytes()));
            await Assert.That(_owner.Quests.ActiveQuests).IsEmpty();
            await Assert.That(_owner.CurrentTarget).IsNull();
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }

    [Test]
    public async Task ClientStartSource_PreservesNonWorldAlternativesAndMatchingSources()
    {
        var template = CreateStartTemplate("npc");
        await Assert.That(CharacterQuests.AllowsClientStartSource(template, QuestAcceptorType.Npc)).IsTrue();
        await Assert.That(CharacterQuests.AllowsClientStartSource(template, QuestAcceptorType.Doodad)).IsFalse();
        var component = template.Components[1];
        component.ActTemplates.Add(new QuestActConAcceptComponent(component));
        await Assert.That(CharacterQuests.AllowsClientStartSource(template, QuestAcceptorType.Unknown)).IsTrue();
        var sphereOnly = new QuestComponentTemplate(template) { Id = 2, KindId = QuestComponentKind.Start };
        sphereOnly.ActTemplates.Add(new QuestActConAcceptSphere(sphereOnly));
        template.Components.Add(sphereOnly.Id, sphereOnly);
        await Assert.That(CharacterQuests.AllowsClientStartSource(template, QuestAcceptorType.Unknown)).IsFalse();
    }

    private static QuestTemplate CreateStartTemplate(string kind)
    {
        var template = new QuestTemplate { Id = 12 };
        var component = new QuestComponentTemplate(template) { Id = 1, KindId = QuestComponentKind.Start };
        component.ActTemplates.Add(kind switch
        {
            "npc" => new QuestActConAcceptNpc(component) { NpcId = 810 },
            "doodad" => new QuestActConAcceptDoodad(component) { DoodadId = 685 },
            _ => new QuestActConAcceptSphere(component) { SphereId = 1 }
        });
        template.Components.Add(component.Id, component);
        return template;
    }

    private Quest AddActiveQuest()
    {
        var template = new QuestTemplate { Id = 12 };
        var quest = new Quest(template, _owner, _manager, Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object, Mock.Of<IExpressTextManager>().Object, Mock.Of<IWorldManager>().Object)
        {
            Step = QuestComponentKind.Ready
        };
        _owner.Quests.ActiveQuests.Add(template.Id, quest);
        return quest;
    }

    private void MakeUnavailable(BaseUnit source, string state)
    {
        switch (state)
        {
            case "hidden":
                source.IsVisible = false;
                break;
            case "unstreamed":
                // Identical region coordinates must not count as the same streamed region.
                source.Region = QuestInteractionTestModels.CreateRegion(_world);
                break;
            case "other_instance":
                SetParentWorld(source, new WorldInstance(_world.Template, 0, true, 2));
                break;
            case "other_world":
                SetParentWorld(source, new WorldInstance(new WorldTemplate { Id = 3 }, 0, true, 3));
                break;
            case "stale_instance":
                typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
                    .GetField("_instanceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(source.Transform, 9U);
                break;
        }
    }

    private static void SetParentWorld(GameObject source, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(source, world);
}
