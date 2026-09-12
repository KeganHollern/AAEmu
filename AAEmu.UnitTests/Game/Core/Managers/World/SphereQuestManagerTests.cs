using System.Globalization;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.IO;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Xml;
using AAEmu.Game.Models.Spheres;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

[NotInParallel]
public sealed class SphereQuestManagerTests
{
    private const uint QuestId = 60001;
    private const uint ComponentId = 60002;
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private readonly List<string> _directories = [];
    private string _clientRoot;
    private ClientSource _source;
    private SphereGameData _sphereData;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new QuestManager(null, null));
        _sphereData = new SphereGameData();
        SetField(_sphereData, "_spheres", new Dictionary<uint, Spheres>());
        SetField(_sphereData, "_sphereQuests", new Dictionary<uint, SphereQuests>());
        SetInstance(_sphereData);
        _clientRoot = Path.Combine(AppContext.BaseDirectory, "sphere-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_clientRoot);
        _directories.Add(_clientRoot);
        ClientFileManager.AddSource(_clientRoot);
        _source = ClientFileManager.Sources[^1];
    }

    [After(Test)]
    public void TearDown()
    {
        ((IList<ClientSource>)ClientFileManager.Sources).Remove(_source);
        _source.Close();
        foreach (var directory in _directories)
            Directory.Delete(directory, true);
        foreach (var (field, value) in _previous)
            field.SetValue(null, value);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Load_TwoWorldsInEitherOrder_KeepsClientAndSupplementGeometryLocal(bool dungeonFirst)
    {
        var main = CreateWorld(1, "main", 1, 2);
        var dungeon = CreateWorld(2, "dungeon", 3, 4);
        var order = dungeonFirst ? new[] { dungeon, main } : [main, dungeon];
        foreach (var world in order)
            world.SphereQuestManager.Load();

        await AssertGeometry(main, new Vector3(1025.5f, 2050.25f, 3.5f));
        await AssertGeometry(dungeon, new Vector3(3073.5f, 4098.25f, 3.5f));
    }

    [Test]
    public async Task Load_TwoWorldsInParallel_KeepsClientAndSupplementGeometryLocal()
    {
        var main = CreateWorld(1, "main", 1, 2);
        var dungeon = CreateWorld(2, "dungeon", 3, 4);
        using var start = new Barrier(2);
        await Task.WhenAll(new[] { main, dungeon }.Select(world => Task.Run(() =>
        {
            start.SignalAndWait();
            world.SphereQuestManager.Load();
        })));

        await AssertGeometry(main, new Vector3(1025.5f, 2050.25f, 3.5f));
        await AssertGeometry(dungeon, new Vector3(3073.5f, 4098.25f, 3.5f));
    }

    [Test]
    public async Task Load_TwoInstancesOfOneTemplate_KeepsTriggerStateSeparate()
    {
        var first = CreateWorld(1, "main", 1, 2);
        var second = new WorldInstance(first.Template, 1, true, 2);
        second.SphereQuestManager = new SphereQuestManager(second);
        first.SphereQuestManager.Load();
        second.SphereQuestManager.Load();
        var owner = CreateCharacter(first, 7);
        var quest = CreateQuest(owner);
        first.SphereQuestManager.AddSphereQuestTriggers(owner, quest, ComponentId, 0);

        await Assert.That(first.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(2);
        await Assert.That(second.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(0);
        await Assert.That(second.SphereQuestManager.GetQuestSpheres(ComponentId).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Load_NonEnglishCulture_DoesNotChangeTheCallerCulture()
    {
        var world = CreateWorld(1, "main", 1, 2);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            world.SphereQuestManager.Load();
            await Assert.That(CultureInfo.CurrentCulture.Name).IsEqualTo("tr-TR");
            await AssertGeometry(world, new Vector3(1025.5f, 2050.25f, 3.5f));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task IsInsideAreaSphere_SameQuestAndCoordinatesInAnotherWorld_ReturnsNoMatch()
    {
        var main = CreateWorld(1, "main", 1, 2);
        var dungeon = CreateWorld(2, "dungeon", 3, 4);
        main.SphereQuestManager.Load();
        dungeon.SphereQuestManager.Load();
        SetField(_sphereData, "_spheres", new Dictionary<uint, Spheres>
        {
            [99] = new() { Id = 99, SphereDetailId = 98, SphereDetailType = "SphereQuest" }
        });
        SetField(_sphereData, "_sphereQuests", new Dictionary<uint, SphereQuests>
        {
            [98] = new() { Id = 98, QuestId = QuestId }
        });
        var mainCenter = main.SphereQuestManager.GetQuestSpheres(ComponentId)[0].Xyz;
        var dungeonCenter = dungeon.SphereQuestManager.GetQuestSpheres(ComponentId)[0].Xyz;

        await Assert.That(_sphereData.IsInsideAreaSphere(99, 1, main, mainCenter, ComponentId)).IsNotNull();
        await Assert.That(_sphereData.IsInsideAreaSphere(99, 1, dungeon, mainCenter, ComponentId)).IsNull();
        await Assert.That(_sphereData.IsInsideAreaSphere(99, 1, dungeon, dungeonCenter, ComponentId)).IsNotNull();
        await Assert.That(_sphereData.IsInsideAreaSphere(99, 1, main, dungeonCenter, ComponentId)).IsNull();
        await Assert.That(_sphereData.IsInsideAreaSphere(99, 1, null, mainCenter, ComponentId)).IsNull();
    }

    [Test]
    public async Task AddSphereQuestTriggers_RepeatedRegistration_KeepsEachDistinctVolumeOnce()
    {
        var world = CreateWorld(1, "main", 1, 2);
        world.SphereQuestManager.Load();
        var owner = CreateCharacter(world, 7);
        var quest = CreateQuest(owner);
        world.SphereQuestManager.AddSphereQuestTriggers(owner, quest, ComponentId, 0, 99);
        world.SphereQuestManager.AddSphereQuestTriggers(owner, quest, ComponentId, 0, 99);
        Tick(world.SphereQuestManager);

        var triggers = world.SphereQuestManager.GetSphereQuestTriggers();
        await Assert.That(triggers.Count).IsEqualTo(2);
        await Assert.That(triggers.Select(trigger => trigger.Sphere.Xyz).Distinct().Count()).IsEqualTo(2);
        await Assert.That(triggers.All(trigger => trigger.SphereId == 99)).IsTrue();
    }

    [Test]
    public async Task RemoveSphereQuestTriggers_BeforeFirstTick_DoesNotLeavePendingTriggers()
    {
        var world = CreateWorld(1, "main", 1, 2);
        world.SphereQuestManager.Load();
        var owner = CreateCharacter(world, 7);
        var otherOwner = CreateCharacter(world, 8);
        world.SphereQuestManager.AddSphereQuestTriggers(owner, CreateQuest(owner), ComponentId, 0);
        world.SphereQuestManager.AddSphereQuestTriggers(otherOwner, CreateQuest(otherOwner), ComponentId, 0);
        world.SphereQuestManager.RemoveSphereQuestTriggers(owner.Id, QuestId);
        Tick(world.SphereQuestManager);

        await Assert.That(world.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(2);
        await Assert.That(world.SphereQuestManager.GetSphereQuestTriggers().All(trigger => trigger.Owner.Id == 8)).IsTrue();
        world.SphereQuestManager.RemoveSphereQuestTriggers(otherOwner.Id, 0);
        Tick(world.SphereQuestManager);
        await Assert.That(world.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Tick_OwnerChangedInstance_RemovesOldWorldTriggers()
    {
        var world = CreateWorld(1, "main", 1, 2);
        world.SphereQuestManager.Load();
        var owner = CreateCharacter(world, 7);
        world.SphereQuestManager.AddSphereQuestTriggers(owner, CreateQuest(owner), ComponentId, 0);
        owner.Transform.InstanceId = 2;
        Tick(world.SphereQuestManager);

        await Assert.That(world.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(0);
    }

    [Test]
    public async Task TickQuestStarters_OverlappingSpheres_ReportsBothEntriesFromTheSamePreviousPosition()
    {
        var world = CreateWorld(1, "main", 1, 2);
        var owner = CreateCharacter(world, 7);
        owner.Transform.Local.Position = new Vector3(20, 0, 0);
        var starters = AddStarters(world, owner, new Vector3(0, 0, 0), new Vector3(1, 0, 0));
        var entries = new List<SphereQuestStarter>();
        world.SphereQuestManager.TickQuestStarters((_, sphere, _) => entries.Add(sphere));
        owner.Transform.Local.Position = Vector3.Zero;
        world.SphereQuestManager.TickQuestStarters((_, sphere, _) => entries.Add(sphere));
        world.SphereQuestManager.TickQuestStarters((_, sphere, _) => entries.Add(sphere));

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(entries.Contains(starters[0]) && entries.Contains(starters[1])).IsTrue();
    }

    [Test]
    public async Task TickQuestStarters_FirstPositionAtOrigin_ReportsEntryOnce()
    {
        var world = CreateWorld(1, "main", 1, 2);
        var owner = CreateCharacter(world, 7);
        AddStarters(world, owner, Vector3.Zero);
        var entries = 0;
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entries++);
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entries++);

        await Assert.That(entries).IsEqualTo(1);
    }

    [Test]
    public async Task Tick_FirstCallbackRemovesQuest_DoesNotTickRemovedSnapshotEntries()
    {
        var world = CreateWorld(1, "main", 1, 2);
        var owner = CreateCharacter(world, 7);
        var region = new Region(world, 0, 0, 7);
        SetField(region, "_playerCount", 1);
        owner.Region = region;
        var quest = CreateQuest(owner);
        var entered = 0;
        owner.Events.OnEnterSphere += (_, _) =>
        {
            entered++;
            world.SphereQuestManager.RemoveSphereQuestTriggers(owner.Id, QuestId);
        };
        for (var index = 0; index < 2; index++)
            world.SphereQuestManager.AddSphereQuestTrigger(new SphereQuestTrigger
            {
                Owner = owner, Quest = quest, Sphere = new SphereQuest { Radius = 5 }, TickRate = 0
            });
        Tick(world.SphereQuestManager);

        await Assert.That(entered).IsEqualTo(1);
        await Assert.That(world.SphereQuestManager.GetSphereQuestTriggers().Count).IsEqualTo(0);
    }

    [Test]
    public async Task TickQuestStarters_LeavesAndReturnsToInstance_ReportsFreshEntry()
    {
        var world = CreateWorld(1, "main", 1, 2);
        var owner = CreateCharacter(world, 7);
        AddStarters(world, owner, Vector3.Zero);
        var entered = 0;
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entered++);
        world.SphereQuestManager.RemoveSphereQuestTriggers(owner.Id, 0);
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entered++);
        owner.Transform.InstanceId = 2;
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entered++);
        owner.Transform.InstanceId = 1;
        world.SphereQuestManager.TickQuestStarters((_, _, _) => entered++);

        await Assert.That(entered).IsEqualTo(3);
    }

    private WorldInstance CreateWorld(uint id, string label, int originX, int originY)
    {
        var name = "sphere_test_" + label + "_" + Guid.NewGuid().ToString("N");
        var world = new WorldInstance(new WorldTemplate { Id = id, Name = name }, 0, true, id);
        world.Template.XmlWorldZones.TryAdd(7, new XmlWorldZone { ZoneKey = 7, OriginX = originX, OriginY = originY });
        world.SphereQuestManager = new SphereQuestManager(world);
        var clientDirectory = Path.Combine(_clientRoot, "game", "worlds", name, "level_design", "zone", "7", "client");
        Directory.CreateDirectory(clientDirectory);
        File.WriteAllText(Path.Combine(clientDirectory, "quest_sign_sphere.g"), $"AREA test\nQTYPE {QuestId}\nCTYPE {ComponentId}\nPOS (X1.5, Y2.25, Z3.5)\nRADIUS 10.5\n");
        var supplementDirectory = Path.Combine(FileManager.AppPath, "Data", "Worlds", name);
        Directory.CreateDirectory(supplementDirectory);
        _directories.Add(supplementDirectory);
        File.WriteAllText(Path.Combine(supplementDirectory, "quest_spheres.json"),
            $$"""[{"QuestId":{{QuestId}},"ComponentId":{{ComponentId}},"ZoneId":7,"x":99,"y":98,"z":97,"radius":5}]""");
        return world;
    }

    private static async Task AssertGeometry(WorldInstance world, Vector3 clientPosition)
    {
        var spheres = world.SphereQuestManager.GetQuestSpheres(ComponentId);
        await Assert.That(spheres.Count).IsEqualTo(2);
        await Assert.That(spheres[0].Xyz).IsEqualTo(clientPosition);
        await Assert.That(spheres[1].Xyz).IsEqualTo(new Vector3(99, 98, 97));
        await Assert.That(spheres.All(sphere => sphere.WorldId == world.Template.Name)).IsTrue();
        await Assert.That(world.SphereQuestManager.GetSpheresForQuest(QuestId).Count).IsEqualTo(2);
        await Assert.That(world.SphereQuestManager.GetQuestSpheres(ComponentId + 1)).IsNull();
    }

    private static List<SphereQuestStarter> AddStarters(WorldInstance world, Character owner, params Vector3[] centers)
    {
        var region = new Region(world, 0, 0, 7);
        SetField(region, "_neighbors", new[] { region });
        SetField(region, "_objects", new GameObject[] { owner });
        SetField(region, "_objectsSize", 1);
        SetField(region, "_playerCount", 1);
        var starters = centers.Select(center => new SphereQuestStarter
        {
            Sphere = new SphereQuest { WorldId = world.Template.Name, QuestId = QuestId, ComponentId = ComponentId, Xyz = center, Radius = 5 },
            Region = region,
            QuestTemplateId = QuestId
        }).ToList();
        GetField<List<SphereQuestStarter>>(world.SphereQuestManager, "_questStartingSpheres").AddRange(starters);
        return starters;
    }

    internal static Character CreateCharacter(WorldInstance world, uint id)
    {
        var owner = new CharacterMock { Id = id, ObjId = id, Transform = new Transform(null) };
        owner.ParentWorld = world;
        return owner;
    }

    internal static Quest CreateQuest(Character owner)
    {
        return new Quest(null, owner, null, null, null, null, null, false) { TemplateId = QuestId, Id = 123 };
    }

    private static void Tick(SphereQuestManager manager)
    {
        typeof(SphereQuestManager).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [TimeSpan.FromMilliseconds(500)]);
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
}
