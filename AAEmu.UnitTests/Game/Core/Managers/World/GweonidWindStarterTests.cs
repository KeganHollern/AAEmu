using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Spheres;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class SphereQuestManagerTests
{
    [Test]
    public async Task GweonidWind_QuestVolumeIsAboveTheWalkableGust()
    {
        var world = new WorldInstance(new WorldTemplate { Name = "main_world", Id = 1 }, 0, true, 1);
        SetInstance(new UnitRequirementsGameData());
        var component = new QuestComponentTemplate(new QuestTemplate { Id = 41 }) { Id = 814, KindId = QuestComponentKind.Start };
        component.ActTemplates.Add(new QuestActConAcceptSphere(component) { SphereId = 101 });
        SetField(QuestManager.Instance, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate> { [814] = component });
        SetField(_sphereData, "_spheres", new Dictionary<uint, Spheres>
        {
            [101] = new() { Id = 101, SphereDetailType = "SphereQuest", SphereDetailId = 101 }
        });
        SetField(_sphereData, "_sphereQuests", new Dictionary<uint, SphereQuests> { [101] = new() { Id = 101, QuestId = 41 } });
        var manager = new SphereQuestManager(world);
        manager.Load();
        var starters = GetField<List<SphereQuestStarter>>(manager, "_questStartingSpheres");
        await Assert.That(starters.Count).IsEqualTo(14);
        await Assert.That(starters.All(s => s.Sphere.ZoneId == 182 && s.Sphere.Radius == 5 && s.SphereId == 101)).IsTrue();

        var owner = CreateCharacter(world, 7);
        var region = new Region(world, 0, 0, 182);
        SetField(region, "_neighbors", new[] { region });
        SetField(region, "_objects", new GameObject[] { owner });
        SetField(region, "_objectsSize", 1);
        SetField(region, "_playerCount", 1);
        foreach (var starter in starters)
            starter.Region = region;
        var entered = new List<SphereQuestStarter>();
        var ground = new Vector3(11140.628f, 14855.227f, 185.874f);
        owner.Transform.Local.Position = ground;
        manager.TickEntrySpheres((_, s, _) => entered.Add(s));
        await Assert.That(entered).IsEmpty();
        owner.Transform.Local.Position = ground + new Vector3(0, 0, 8);
        manager.TickEntrySpheres((_, s, _) => entered.Add(s));
        manager.TickEntrySpheres((_, s, _) => entered.Add(s));
        await Assert.That(entered.Count).IsEqualTo(1);
        await Assert.That(entered[0].QuestTemplateId).IsEqualTo(41u);
    }

    [Test]
    [Arguments(101)]
    [Arguments(303)]
    public async Task ExactClient_GweonidWind_RegistersEachOriginalVolumeOnce(int sphereId)
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for original mission geometry.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            var world = new WorldInstance(new WorldTemplate { Name = "main_world", Id = 1 }, 0, true, 1);
            var manager = new SphereQuestManager(world);
            SeedGroundWindSphere();
            manager.Load();
            var actual = sphereId == 101
                ? manager.GetSpheresForQuest(41).Select(s => (Center: s.Xyz, s.Radius)).ToList()
                : GetField<List<WorldSkillSphere>>(manager, "_skillSpheres").Select(s => (s.Center, s.Radius)).ToList();
            using var worldFile = source.GetFileStream("game/worlds/main_world/world.xml");
            var zone = XDocument.Load(worldFile).Descendants("Zone").Single(z => (int?)z.Attribute("id") == 129);
            var offset = new Vector3((int)zone.Attribute("originX")! * WorldManager.CELL_SIZE,
                (int)zone.Attribute("originY")! * WorldManager.CELL_SIZE, 0);
            using var missionFile = source.GetFileStream("game/worlds/main_world/zone/129/mission_mission0.xml");
            var mission = XDocument.Load(missionFile);
            var spheres = mission.Descendants("Entity").Where(e =>
                (string)e.Attribute("EntityClass") == "AreaSphere" && (int?)e.Element("Area")?.Attribute("Group") == 16);

            // ObjectsSyncless repeats the same areas from Objects.
            var originals = spheres.Where(e => (int)e.Element("Area")!.Attribute("value1")! == sphereId)
                .GroupBy(e => (string)e.Attribute("EntityGuid")).ToArray();
            await Assert.That(originals.Length).IsEqualTo(14);
            await Assert.That(actual.Count).IsEqualTo(originals.Length);
            foreach (var copies in originals)
            {
                var entity = copies.First();
                var area = entity.Element("Area")!;
                await Assert.That((string)area.Attribute("SphereCenter")).IsEqualTo("0,0,0");
                var xyz = entity.Attribute("Pos")!.Value.Split(',').Select(n => float.Parse(n, CultureInfo.InvariantCulture)).ToArray();
                var center = offset + new Vector3(xyz[0], xyz[1], xyz[2]);
                var radius = (float)area.Attribute("SphereRadius")!;
                await Assert.That(actual.Count(v => v.Radius == radius && Vector3.Distance(v.Center, center) < 0.002f)).IsEqualTo(1);
            }
        }
        finally
        {
            source.Close();
        }
    }
}
