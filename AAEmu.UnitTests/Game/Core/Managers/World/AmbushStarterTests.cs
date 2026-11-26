using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Xml.Linq;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Spheres;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class SphereQuestManagerTests
{
    [Test]
    public async Task ExactClient_AmbushFarm_RepeatsOneTypedTenPointArea()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for original mission geometry.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var world = new WorldInstance(new WorldTemplate { Name = "main_world", Id = 1 }, 0, true, 1);
            world.SphereQuestManager = new SphereQuestManager(world);
            world.SphereQuestManager.Load();
            var volume = world.SphereQuestManager.GetQuestSpheres(2541).Single();
            using var worldFile = source.GetFileStream("game/worlds/main_world/world.xml");
            var zone = XDocument.Load(worldFile).Root!.Element("ZoneList")!.Elements("Zone")
                .Single(z => (uint?)z.Attribute("id") == volume.ZoneId);
            var offset = new Vector3((int)zone.Attribute("originX")! * WorldManager.CELL_SIZE,
                (int)zone.Attribute("originY")! * WorldManager.CELL_SIZE, 0);
            using var missionFile = source.GetFileStream("game/worlds/main_world/zone/133/mission_mission0.xml");
            var mission = XDocument.Load(missionFile).Root!;
            var shapes = mission.Descendants("Entity").Where(e => (string)e.Attribute("EntityGuid") == "47729991A4CD58C2").ToArray();
            await Assert.That(shapes.Select(e => e.Parent!.Name.LocalName)).IsEquivalentTo(new[] { "Objects", "ObjectsSyncless" });
            await Assert.That(XNode.DeepEquals(shapes[0], shapes[1])).IsTrue();
            var shape = shapes[0];
            await Assert.That((string)shape.Attribute("EntityClass")).IsEqualTo("AreaShape");
            var area = shape.Element("Area")!;
            await Assert.That((int)area.Attribute("Group")!).IsEqualTo(16);
            await Assert.That((int)area.Attribute("Id")!).IsEqualTo(0);
            await Assert.That((int)area.Attribute("value1")!).IsEqualTo(120);
            await Assert.That((int)area.Attribute("value2")!).IsEqualTo(0);
            var origin = offset + Position(shape);
            var vertices = area.Element("Points")!.Elements("Point").Select(point => origin + Position(point)).ToList();
            await Assert.That(vertices.Count).IsEqualTo(10);
            await Assert.That(volume.Xyz).IsEqualTo(origin);
            await Assert.That(volume.Polygon.Height).IsEqualTo((float)area.Attribute("Height")!);
            await Assert.That(volume.Polygon.Points.SequenceEqual(vertices)).IsTrue();
        }
        finally
        {
            source.Close();
        }

        static Vector3 Position(XElement element)
        {
            var parts = ((string)element.Attribute("Pos")!).Split(',');
            return new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
        }
    }

    [Test]
    public async Task Load_AmbushFarm_UsesExactPolygonAndAuthoredVerticalExtent()
    {
        var world = new WorldInstance(new WorldTemplate { Name = "main_world", Id = 1 }, 0, true, 1);
        world.SphereQuestManager = new SphereQuestManager(world);
        world.SphereQuestManager.Load();
        var volumes = world.SphereQuestManager.GetQuestSpheres(2541);
        await Assert.That(volumes?.Count ?? 0).IsEqualTo(1);
        var volume = volumes.Single();
        await Assert.That(volume.QuestId).IsEqualTo(340u);
        await Assert.That(volume.ZoneId).IsEqualTo(133u);
        // The first point is at Adrian. The third point is in the concave notch.
        await Assert.That(volume.Contains(new Vector3(11477.12f, 11517.38f, 129.86136f))).IsFalse();
        await Assert.That(volume.Contains(new Vector3(11476, 11527, 129))).IsTrue();
        await Assert.That(volume.Contains(new Vector3(11476, 11535, 129))).IsFalse();
        foreach (var z in new[] { -100f, 400f })
        {
            await Assert.That(volume.Contains(new Vector3(11476, 11527, z))).IsTrue();
            await Assert.That(volume.Contains(new Vector3(11476, 11535, z))).IsFalse();
        }
    }

    [Test]
    [Arguments(22, 148, true, true)]
    [Arguments(21, 148, true, false)]
    [Arguments(22, 149, true, false)]
    [Arguments(22, 148, false, false)]
    public async Task Tick_AmbushFarm_ChecksStockRequirementsAndStartsOnce(int level, int faction, bool completedClue, bool eligible)
    {
        using var models = new QuestInteractionTestModels();
        var world = new WorldInstance(new WorldTemplate { Name = "main_world", Id = 1 }, 0, true, 1);
        world.SphereQuestManager = new SphereQuestManager(world);
        var owner = new AmbushWitness
        {
            Id = 7, ObjId = 70, ModelId = 1, Level = (byte)level, Name = "Questor",
            Faction = new SystemFaction { MotherId = (FactionsEnum)faction }, Transform = new Transform(null)
        };
        var saved = new List<Quest>();
        owner.ParentWorld = world;
        owner.Quests = new CharacterQuests(owner, _ => true, _ => { }, saved.Add, new GameScheduleManager(null, null));
        if (completedClue)
            owner.Quests.SetCompletedQuestFlag(336, true, _ => true, out _, out _);
        var template = new QuestTemplate { Id = 340 };
        var accept = new QuestComponentTemplate(template) { Id = 2541, KindId = QuestComponentKind.Start, SkillId = 11409, SkillSelf = true };
        accept.ActTemplates.Add(new QuestActConAcceptSphere(accept) { ActId = 3867, SphereId = 120 });
        var report = new QuestComponentTemplate(template) { Id = 2543, KindId = QuestComponentKind.Ready };
        report.ActTemplates.Add(new QuestActConReportNpc(report) { ActId = 14827, NpcId = 5157 });
        template.Components.Add(2541, accept);
        template.Components.Add(2543, report);
        template.Components.Add(2544, new QuestComponentTemplate(template) { Id = 2544, KindId = QuestComponentKind.Reward });
        SetField(QuestManager.Instance, "_questTemplates", new Dictionary<uint, QuestTemplate> { [340] = template });
        SetField(QuestManager.Instance, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate> { [2541] = accept });
        SetField(_sphereData, "_spheres", new Dictionary<uint, Spheres>
        {
            [120] = new() { Id = 120, SphereDetailId = 120, SphereDetailType = "SphereQuest", EnterOrLeave = true }
        });
        SetField(_sphereData, "_sphereQuests", new Dictionary<uint, SphereQuests> { [120] = new() { Id = 120, QuestId = 340 } });
        var requirements = new UnitRequirementsGameData();
        SetInstance(requirements);
        typeof(UnitRequirementsGameData).GetProperty("_unitReqsByOwnerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(requirements, new Dictionary<string, List<UnitReqs>>
            {
                ["QuestComponent"] =
                [
                    new() { OwnerId = 2541, KindType = UnitReqsKindType.Level, Value1 = 22 },
                    new() { OwnerId = 2541, KindType = UnitReqsKindType.MotherFaction, Value1 = 148 },
                    new() { OwnerId = 2541, KindType = UnitReqsKindType.CompleteQuestContext, Value1 = 336 }
                ]
            });
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new TaskManager(Mock.Of<ITickManager>().Object));
        SetInstance(new ExpressTextManager());
        SetInstance(new WorldManager(Mock.Of<ITickManager>().Object, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));
        var ids = new QuestIdManager();
        typeof(IdManager).GetField("_freeIds", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ids, new BitSet(32));
        var idField = typeof(QuestIdManager).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _previous.Add(idField, idField.GetValue(null));
        idField.SetValue(null, ids);

        world.SphereQuestManager.Load();
        var starters = GetField<List<SphereQuestStarter>>(world.SphereQuestManager, "_questStartingSpheres");
        await Assert.That(starters.Count).IsEqualTo(1);
        // The entity origin, outside foot and inside foot occupy three neighboring regions.
        world.Template.CellX = world.Template.CellY = 12;
        var sectors = 12 * WorldManager.SECTORS_PER_CELL;
        world.Template.ZoneKeyByRegions = new uint[sectors, sectors];
        world.Regions = new Region[sectors, sectors];
        foreach (var position in new[] { starters[0].Sphere.Xyz, new Vector3(11476, 11519, 129.346f), new Vector3(11476, 11527, 127.619f) })
        {
            var x = (int)(position.X / WorldManager.REGION_SIZE);
            var y = (int)(position.Y / WorldManager.REGION_SIZE);
            world.Template.ZoneKeyByRegions[x, y] = 133;
            world.Regions[x, y] = new Region(world, x, y, 133);
        }
        void Visit(float y, float z)
        {
            owner.Transform.Local.Position = new Vector3(11476, y, z);
            var region = world.GetRegionByPos(owner.Transform.World.Position);
            if (owner.Region != region)
            {
                owner.Region?.RemoveObject(owner);
                owner.Region = region;
                region.AddObject(owner);
            }
            world.SphereQuestManager.TickEntrySpheres(QuestManager.Instance.DoOnEnterQuestStarterSphere);
        }

        Visit(11519, 129.346f);
        await Assert.That(owner.Quests.HasQuest(340)).IsFalse();
        Visit(11527, 127.619f);
        Visit(11527, 127.619f);
        Visit(11519, 129.346f);
        Visit(11527, 127.619f);
        await Assert.That(owner.Quests.HasQuest(340)).IsEqualTo(eligible);
        await Assert.That(saved.Select(quest => quest.Id).Distinct().Count()).IsEqualTo(eligible ? 1 : 0);
        await Assert.That(owner.SelfSkills.Count).IsEqualTo(eligible ? 1 : 0);
        if (eligible)
        {
            var quest = owner.Quests.ActiveQuests[340];
            await Assert.That(quest.QuestAcceptorType).IsEqualTo(QuestAcceptorType.Sphere);
            await Assert.That(quest.AcceptorId).IsEqualTo(120u);
            await Assert.That(owner.SelfSkills.Single()).IsEqualTo(11409u);
            quest.FinalizeQuestActs();
        }
    }

    private sealed class AmbushWitness : CharacterMock
    {
        public List<uint> SelfSkills { get; } = [];

        public override SkillResult UseSkill(uint skillId, IUnit target)
        {
            if (ReferenceEquals(this, target))
                SelfSkills.Add(skillId);
            return SkillResult.Success;
        }
    }
}
