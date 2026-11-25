using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Xml;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class ArenaWorldRuntimeTests : IDisposable
{
    private readonly Mock<IWorldIdManager> _ids = new();
    private readonly TickManager _ticks = new();
    private readonly TaskManager _tasks;
    private readonly WorldManager _worlds;
    private readonly WorldConfig _worldConfig = AppConfiguration.Instance.World;
    private readonly List<(FieldInfo Field, object Value)> _originals = [];
    private readonly List<WorldInstance> _created = [];
    private readonly WorldTemplate _template = new() { Id = 1, Name = "arena_runtime_test", CellX = 1, CellY = 1, ZoneKeys = [302] };
    private readonly Battlefield _field = new() { Id = 11, ZoneKey = 302, RuleSet = new() { Corps1FactionId = 159, Corps2FactionId = 160 } };

    public ArenaWorldRuntimeTests()
    {
        AppConfiguration.Instance.World = new WorldConfig();
        _tasks = new TaskManager(_ticks);
        _worlds = new WorldManager(null, _ids.Object, null, null, null);
        _template.XmlWorld.IsInstance = 1;
        _template.XmlWorld.Zones = new() { [302] = new XmlWorldZone() };
        _template.Cells[0, 0] = new WorldCell(0, 0, _template);
        _worlds.WorldTemplates.Add(_template.Name, _template);
        typeof(WorldManager).GetField("<WorldNames>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_worlds, new List<string> { "main_world", _template.Name });
        typeof(WorldManager).GetField("_worldIdByZoneKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_worlds, new Dictionary<uint, uint> { [302] = _template.Id });
        Install(_ticks);
        Install(_tasks);
        Install(_worlds);
        Install(new InstantGameManager());
        Install(new ShipyardManager(_tasks, null, null, _worlds, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndGame_StopsOnlyItsDedicatedWorld(bool registryIdReused)
    {
        _ids.SetupSequence(manager => manager.GetNextId()).Returns(200u).Returns(registryIdReused ? 200u : 201u);
        var first = new InstantGame(_field);
        var retired = Assert.Single(_worlds.GetWorlds());
        _created.Add(retired);
        if (registryIdReused)
            _worlds.RemoveWorld(retired.Id);
        _ = new InstantGame(_field);
        var active = Assert.Single(_worlds.GetWorlds(), world => world != retired);
        _created.Add(active);

        await ApplyTicks();
        Assert.True(retired.Physics._thread.IsAlive);
        Assert.True(active.Physics._thread.IsAlive);
        Assert.Equal(3, TickCount(retired));
        Assert.Equal(3, TickCount(active));
        Assert.Equal(2, _tasks.GetQueueCount());
        Assert.True(RespawnEnabled(retired));

        await first.EndGame();
        await ApplyTicks();
        Assert.Same(active, Assert.Single(_worlds.GetWorlds()));
        Assert.True(active.Physics._thread.IsAlive);
        Assert.True(RespawnEnabled(active));
        Assert.Equal(3, TickCount(active));
        var stopped = retired.Physics._thread.Join(TimeSpan.FromSeconds(2));
        var callbacks = TickCount(retired);
        var repeats = _tasks.GetQueueCount();
        Assert.True(stopped && callbacks == 0 && repeats == 1,
            $"Retired physics alive={!stopped}, retired tick callbacks={callbacks}, scheduled tasks for both worlds={repeats}.");
        Assert.False(RespawnEnabled(retired));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndGame_PreservesTheBorrowedSharedWorld(bool fixedWorldId)
    {
        _ids.Setup(manager => manager.GetNextId()).Returns(200u);
        _template.XmlWorld.IsInstance = 0;
        _template.XmlWorld.Zones[303] = new XmlWorldZone();
        var shared = _worlds.CreateWorldInstance(_template, 0, fixedWorldId, fixedWorldId ? 42u : 0u);
        _created.Add(shared);
        var match = new InstantGame(_field);
        Assert.Same(shared, Assert.Single(_worlds.GetWorlds()));
        await match.EndGame();
        await ApplyTicks();

        Assert.Same(shared, Assert.Single(_worlds.GetWorlds()));
        Assert.True(shared.Physics._thread.IsAlive);
        Assert.True(RespawnEnabled(shared));
        Assert.Equal(3, TickCount(shared));
        Assert.Equal(1, _tasks.GetQueueCount());
    }

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _originals.Add((field, field.GetValue(null)));
        field.SetValue(null, manager);
    }

    private static bool RespawnEnabled(WorldInstance world) =>
        (bool)typeof(SpawnManager).GetField("_work", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world.SpawnManager)!;

    private List<TickManager.TickEventEntity> Registrations =>
        (List<TickManager.TickEventEntity>)typeof(TickManager.TickEventHandler)
            .GetField("_eventList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_ticks.OnTick)!;

    private int TickCount(WorldInstance world) => Registrations.Count(entry =>
        entry.Event.Target == world.GimmickManager || entry.Event.Target == world.SphereQuestManager ||
        entry.Event.Target == world.TransferManager);

    private async Task ApplyTicks()
    {
        _ticks.OnTick.Invoke();
        await Task.WhenAll(Registrations.Select(entry => entry.ActiveTask).OfType<Task>());
    }

    public void Dispose()
    {
        foreach (var world in _worlds.GetWorlds().Concat(_created).Distinct())
        {
            world.SpawnManager?.Stop();
            world.Physics?.Stop();
            if (world.Physics?._thread?.Join(TimeSpan.FromSeconds(3)) == true)
                world.Physics.Dispose();
            GC.SuppressFinalize(world);
        }
        AppConfiguration.Instance.World = _worldConfig;
        foreach (var original in _originals)
            original.Field.SetValue(null, original.Value);
    }
}
