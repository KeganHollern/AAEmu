using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Indun;
using AAEmu.Game.Models.Game.Indun.Actions;
using AAEmu.Game.Models.Game.Indun.Events;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.Indun;

[NotInParallel]
public sealed class DungeonLifecycleTests
{
    private const uint ZoneGroupId = 55;
    private const uint DoodadTemplateId = 7489;
    private const uint NpcTemplateId = 13433;
    private readonly Dictionary<FieldInfo, object> _previousSingletons = [];
    private readonly List<WorldInstance> _worlds = [];
    private readonly List<IndunEvent> _events = [];
    private readonly Dictionary<uint, IndunRoom> _rooms = [];
    private WorldManager _worldManager;
    private CountingAction _action;

    [Before(Test)]
    public void SetUp()
    {
        var tick = new TickManager();
        _worldManager = new WorldManager(tick, Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var data = new IndunGameData();
        _action = new CountingAction { Id = 1 };
        _events.Clear();
        _rooms.Clear();
        _rooms.Add(7, new IndunRoom { Id = 7, DoodadId = DoodadTemplateId, Radius = 65 });
        SetField(data, "_indunEvents", new Dictionary<uint, List<IndunEvent>> { [ZoneGroupId] = _events });
        SetField(data, "_indunActions", new Dictionary<uint, IndunAction> { [_action.Id] = _action });
        SetField(data, "_indunRooms", _rooms);
        var friends = new FriendMananger();
        SetField(friends, "_allFriends", new Dictionary<uint, FriendTemplate>());

        SetSingleton(data);
        SetSingleton(tick);
        SetSingleton(_worldManager);
        SetSingleton(new SusManager(_worldManager));
        SetSingleton(friends);
        SetSingleton(new FishSchoolManager());
        SetSingleton(new IndunManager(tick, _worldManager, Mock.Of<IZoneManager>().Object,
            Mock.Of<ITeamManager>().Object, TimeProvider.System, Options.Create(new AppConfiguration())));
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var world in _worlds)
        {
            world.DungeonInstance?.UnregisterIndunEvents();
            foreach (var ev in _events)
                ev.UnSubscribe(world);
            GC.SuppressFinalize(world);
        }
        _worlds.Clear();
        foreach (var (field, value) in _previousSingletons)
            field.SetValue(null, value);
        _previousSingletons.Clear();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task ExecuteAsync_EachSpawnGroupCanFinishLast_BindsRoomBeforeReadyAndEntry(int lastGroup)
    {
        var world = CreateWorld();
        var roomEvent = new IndunEventNoAliveChInRooms { RoomId = 7, StartActionId = 1 };
        _events.Add(roomEvent);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        var player = CreatePlayer();
        dungeon.EnterRequests.Add(player);
        var gates = CreateSpawnGates();
        Doodad roomDoodad = null;

        async Task SpawnGroup(int index)
        {
            await gates[index].Task;
            if (index == lastGroup)
                roomDoodad = AddRoomDoodad(world);
        }

        // SpawnAll starts its five real groups as well. These pending group results make
        // the loader wait observable even when an empty test world's real groups finish fast.
        world.SpawnManager = new SpawnManager(world)
        {
            SpawnTasks = Enumerable.Range(0, 5).Select(SpawnGroup).ToList()
        };
        var loading = new DungeonLoaderTask(dungeon).ExecuteAsync();

        try
        {
            for (var i = 0; i < gates.Length; i++)
                if (i != lastGroup)
                    gates[i].SetResult();

            await Assert.That(loading.IsCompleted).IsFalse();
            await Assert.That(dungeon.FinishedLoading).IsFalse();
            await Assert.That(HandlerCount(world, roomEvent)).IsEqualTo(0);
            await Assert.That(roomEvent.GetRoomDoodad(world.Id)).IsNull();
            await Assert.That(world.GetCharacterCount()).IsEqualTo(0);
            await Assert.That(player.Transform.InstanceId == world.Id).IsFalse();
            await Assert.That(dungeon.EnterRequests.Contains(player)).IsTrue();
        }
        finally
        {
            foreach (var gate in gates)
                gate.TrySetResult();
            await Task.WhenAll(world.SpawnManager.SpawnTasks).WaitAsync(TimeSpan.FromSeconds(5));
        }

        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(dungeon.FinishedLoading).IsTrue();
        await Assert.That(roomEvent.GetRoomDoodad(world.Id)).IsSameReferenceAs(roomDoodad);
        await Assert.That(HandlerCount(world, roomEvent)).IsEqualTo(1);
        await Assert.That(world.GetCharacterCount()).IsEqualTo(1);
        await Assert.That(player.Transform.InstanceId).IsEqualTo(world.Id);
        await Assert.That(dungeon.EnterRequests).IsEmpty();

        dungeon.CompleteLoading(world);
        dungeon.RegisterIndunEvents();
        await Assert.That(HandlerCount(world, roomEvent)).IsEqualTo(1);
        await Assert.That(player.Events.OnDungeonLeave.GetInvocationList().Count(handler => handler.Target == dungeon)).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task ExecuteAsync_SpawnFailure_WaitsForOtherGroupsAndPropagatesBeforeEventsOrEntry(int failedGroup)
    {
        var world = CreateWorld();
        var ev = new IndunEventDoodadSpawneds { DoodadAlmightyId = DoodadTemplateId, StartActionId = 1 };
        _events.Add(ev);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        var player = CreatePlayer();
        dungeon.EnterRequests.Add(player);
        var gates = CreateSpawnGates();
        world.SpawnManager = new SpawnManager(world) { SpawnTasks = gates.Select(gate => gate.Task).ToList() };
        var loading = new DungeonLoaderTask(dungeon).ExecuteAsync();
        var expected = new InvalidOperationException("spawn group failed");
        gates[failedGroup].SetException(expected);

        try
        {
            await Assert.That(loading.IsCompleted).IsFalse();
            await Assert.That(dungeon.FinishedLoading).IsFalse();
            await Assert.That(HandlerCount(world, ev)).IsEqualTo(0);
            await Assert.That(world.GetCharacterCount()).IsEqualTo(0);
        }
        finally
        {
            foreach (var gate in gates)
                gate.TrySetResult();
        }

        var actual = await CatchFailure(loading);

        await Assert.That(actual).IsSameReferenceAs(expected);
        await Assert.That(dungeon.FinishedLoading).IsFalse();
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(0);
        await Assert.That(player.Transform.InstanceId == world.Id).IsFalse();
        await Assert.That(world.GetCharacterCount()).IsEqualTo(0);
        world.Events.OnDoodadSpawn(world, new OnDoodadSpawnArgs { Doodad = new Doodad { TemplateId = DoodadTemplateId } });
        await Assert.That(_action.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("doodad")]
    [Arguments("npc_spawn")]
    [Arguments("npc_killed")]
    [Arguments("combat_start")]
    [Arguments("combat_end")]
    [Arguments("room")]
    public async Task Subscribe_RepeatedLifecycle_HasOneHandlerAndDetachesIt(string kind)
    {
        var world = CreateWorld();
        AddRoomDoodad(world);
        var ev = CreateEvent(kind);
        _events.Add(ev);

        ev.Subscribe(world);
        ev.Subscribe(world);
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(1);
        RaiseEvent(kind, world);
        if (kind is "doodad" or "npc_spawn" or "npc_killed")
            await Assert.That(_action.Calls).IsEqualTo(1);

        ev.UnSubscribe(world);
        ev.UnSubscribe(world);
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(0);
        RaiseEvent(kind, world);
        if (kind is "doodad" or "npc_spawn" or "npc_killed")
            await Assert.That(_action.Calls).IsEqualTo(1);

        ev.Subscribe(world);
        ev.Subscribe(world);
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(1);
        RaiseEvent(kind, world);
        if (kind is "doodad" or "npc_spawn" or "npc_killed")
            await Assert.That(_action.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Subscribe_ConcurrentCalls_HasOneHandlerAndOneAction()
    {
        var world = CreateWorld();
        var ev = new IndunEventDoodadSpawneds { DoodadAlmightyId = DoodadTemplateId, StartActionId = 1 };
        _events.Add(ev);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ev.Subscribe(world))));
        RaiseEvent("doodad", world);
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(1);
        await Assert.That(_action.Calls).IsEqualTo(1);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ev.UnSubscribe(world))));
        RaiseEvent("doodad", world);
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(0);
        await Assert.That(_action.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteAsync_EventRegistrationFails_RemovesPartialHandlersBeforeReadiness()
    {
        var world = CreateWorld();
        var first = CreateEvent("doodad");
        var failing = new FailingEvent();
        _events.Add(first);
        _events.Add(failing);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);

        var failure = await CatchFailure(new DungeonLoaderTask(dungeon).ExecuteAsync());

        await Assert.That(failure).IsSameReferenceAs(failing.Failure);
        await Assert.That(dungeon.FinishedLoading).IsFalse();
        await Assert.That(HandlerCount(world, first)).IsEqualTo(0);
        await Assert.That(HandlerCount(world, failing)).IsEqualTo(0);
        RaiseEvent("doodad", world);
        await Assert.That(_action.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task DestroyDungeon_RepeatedTeardown_RemovesHandlersAndRoomStateWithoutReregistering()
    {
        var world = CreateWorld();
        AddRoomDoodad(world);
        var doodadEvent = CreateEvent("doodad");
        var roomEvent = (IndunEventNoAliveChInRooms)CreateEvent("room");
        _events.Add(doodadEvent);
        _events.Add(roomEvent);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        await new DungeonLoaderTask(dungeon).ExecuteAsync();
        RaiseEvent("doodad", world);
        await Assert.That(_action.Calls).IsEqualTo(1);

        dungeon.DestroyDungeon();
        dungeon.DestroyDungeon();
        dungeon.RegisterIndunEvents();
        dungeon.CompleteLoading(world);
        await new DungeonLoaderTask(dungeon).ExecuteAsync();
        RaiseEvent("doodad", world);

        await Assert.That(dungeon.IsDestroyed).IsTrue();
        await Assert.That(dungeon.FinishedLoading).IsFalse();
        await Assert.That(dungeon.World).IsNull();
        await Assert.That(HandlerCount(world, doodadEvent)).IsEqualTo(0);
        await Assert.That(HandlerCount(world, roomEvent)).IsEqualTo(0);
        await Assert.That(roomEvent.GetRoomDoodad(world.Id)).IsNull();
        await Assert.That(_action.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task AreaClearTick_AfterLoadingAndRepeatedRegistration_ExecutesRoomActionOnce()
    {
        var world = CreateWorld();
        AddRoomDoodad(world);
        var roomEvent = (IndunEventNoAliveChInRooms)CreateEvent("room");
        _events.Add(roomEvent);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        InvokeAreaClearTick(dungeon);
        await Assert.That(_action.Calls).IsEqualTo(0);
        await new DungeonLoaderTask(dungeon).ExecuteAsync();
        roomEvent.SetRoomPlayerCount(world.Id, 1);
        dungeon.RegisterIndunEvents();

        InvokeAreaClearTick(dungeon);
        InvokeAreaClearTick(dungeon);

        await Assert.That(_action.Calls).IsEqualTo(1);
        await Assert.That(roomEvent.GetRoomPlayerCount(world.Id)).IsEqualTo(0u);
    }

    [Test]
    public async Task AreaClearTick_FirstRoomAlreadyCleared_StillProcessesLaterRooms()
    {
        var world = CreateWorld();
        AddRoomDoodad(world);
        _rooms.Add(8, new IndunRoom { Id = 8, DoodadId = DoodadTemplateId, Radius = 65 });
        var first = new IndunEventNoAliveChInRooms { RoomId = 7, StartActionId = 1 };
        var second = new IndunEventNoAliveChInRooms { RoomId = 8, StartActionId = 1 };
        _events.Add(first);
        _events.Add(second);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        await new DungeonLoaderTask(dungeon).ExecuteAsync();
        dungeon.SetRoomCleared(first.RoomId);
        second.SetRoomPlayerCount(world.Id, 1);

        InvokeAreaClearTick(dungeon);
        InvokeAreaClearTick(dungeon);

        await Assert.That(_action.Calls).IsEqualTo(1);
        await Assert.That(second.GetRoomPlayerCount(world.Id)).IsEqualTo(0u);
    }

    [Test]
    public async Task ExecuteAsync_DestroyedWhileSpawning_DoesNotRegisterOrBecomeReady()
    {
        var world = CreateWorld();
        var ev = CreateEvent("doodad");
        _events.Add(ev);
        var dungeon = new Dungeon(new IndunZone { ZoneGroupId = ZoneGroupId }, world);
        var gates = CreateSpawnGates();
        world.SpawnManager = new SpawnManager(world) { SpawnTasks = gates.Select(gate => gate.Task).ToList() };
        var loading = new DungeonLoaderTask(dungeon).ExecuteAsync();

        try
        {
            dungeon.DestroyDungeon();
            await Assert.That(loading.IsCompleted).IsFalse();
        }
        finally
        {
            foreach (var gate in gates)
                gate.TrySetResult();
        }
        await loading.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(dungeon.World).IsNull();
        await Assert.That(dungeon.FinishedLoading).IsFalse();
        await Assert.That(HandlerCount(world, ev)).IsEqualTo(0);
    }

    [Test]
    public async Task RoomEvent_TwoWorlds_UnregisteringOnePreservesTheOther()
    {
        var first = CreateWorld();
        var second = CreateWorld();
        AddRoomDoodad(first);
        var secondDoodad = AddRoomDoodad(second);
        var ev = (IndunEventNoAliveChInRooms)CreateEvent("room");
        _events.Add(ev);
        ev.Subscribe(first);
        ev.Subscribe(second);
        ev.SetRoomPlayerCount(first.Id, 3);
        ev.SetRoomPlayerCount(second.Id, 7);

        ev.UnSubscribe(first);
        ev.UnSubscribe(first);
        ev.Subscribe(second);

        await Assert.That(HandlerCount(first, ev)).IsEqualTo(0);
        await Assert.That(HandlerCount(second, ev)).IsEqualTo(1);
        await Assert.That(ev.GetRoomDoodad(first.Id)).IsNull();
        await Assert.That(ev.GetRoomDoodad(second.Id)).IsSameReferenceAs(secondDoodad);
        await Assert.That(ev.GetRoomPlayerCount(second.Id)).IsEqualTo(7u);
    }

    private WorldInstance CreateWorld()
    {
        var template = new WorldTemplate { Id = 1, Name = "test_dungeon", ZoneKeys = [50] };
        var world = new WorldInstance(template, 0, true, (uint)_worlds.Count + 1);
        world.Regions = new Region[1, 1];
        world.Regions[0, 0] = new Region(world, 0, 0, 50);
        world.SpawnManager = new SpawnManager(world);
        _worlds.Add(world);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_worldManager)!;
        worlds.TryAdd(world.Id, world);
        return world;
    }

    private static Doodad AddRoomDoodad(WorldInstance world)
    {
        var doodad = new Doodad { ObjId = 100, TemplateId = DoodadTemplateId, Transform = null };
        world.Regions[0, 0].AddObject(doodad);
        return doodad;
    }

    private static Character CreatePlayer()
    {
        return new Character(null) { Id = 7, ObjId = 7, Name = "Dungeon player", IsOnline = true };
    }

    private static TaskCompletionSource[] CreateSpawnGates()
    {
        return Enumerable.Range(0, 5)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
    }

    private static async Task<Exception> CatchFailure(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IndunEvent CreateEvent(string kind)
    {
        return kind switch
        {
            "doodad" => new IndunEventDoodadSpawneds { DoodadAlmightyId = DoodadTemplateId, StartActionId = 1 },
            "npc_spawn" => new IndunEventNpcSpawneds { NpcId = NpcTemplateId, StartActionId = 1 },
            "npc_killed" => new IndunEventNpcKilleds { NpcId = NpcTemplateId, StartActionId = 1 },
            "combat_start" => new IndunEventNpcCombatStarteds { NpcId = NpcTemplateId, StartActionId = 1 },
            "combat_end" => new IndunEventNpcCombatEndeds { NpcId = NpcTemplateId, StartActionId = 1 },
            "room" => new IndunEventNoAliveChInRooms { RoomId = 7, StartActionId = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void RaiseEvent(string kind, WorldInstance world)
    {
        var npc = new Npc { TemplateId = NpcTemplateId };
        switch (kind)
        {
            case "doodad":
                world.Events.OnDoodadSpawn(world, new OnDoodadSpawnArgs { Doodad = new Doodad { TemplateId = DoodadTemplateId } });
                break;
            case "npc_spawn":
                world.Events.OnUnitSpawn(world, new OnUnitSpawnArgs { Npc = npc });
                break;
            case "npc_killed":
                world.Events.OnUnitKilled(world, new OnUnitKilledArgs { Victim = npc });
                break;
            case "combat_start":
                world.Events.OnUnitCombatStart(world, new OnUnitCombatStartArgs { Npc = npc });
                break;
            case "combat_end":
                world.Events.OnUnitCombatEnd(world, new OnUnitCombatEndArgs { Npc = npc });
                break;
            case "room":
                world.Events.OnAreaClear(world, new OnAreaClearArgs());
                break;
        }
    }

    private static int HandlerCount(WorldInstance world, IndunEvent ev)
    {
        Delegate[] handlers = [world.Events.OnDoodadSpawn, world.Events.OnUnitSpawn, world.Events.OnUnitKilled,
            world.Events.OnUnitCombatStart, world.Events.OnUnitCombatEnd, world.Events.OnAreaClear];
        return handlers.Sum(handler => handler.GetInvocationList().Count(callback => callback.Target == ev));
    }

    private static void InvokeAreaClearTick(Dungeon dungeon)
    {
        typeof(Dungeon).GetMethod("AreaClearTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dungeon, [TimeSpan.FromSeconds(1)]);
    }

    private void SetSingleton<T>(T value) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousSingletons.Add(field, field.GetValue(null));
        field.SetValue(null, value);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class CountingAction : IndunAction
    {
        public int Calls { get; private set; }

        public override void Execute(WorldInstance worldInstance)
        {
            Calls++;
        }
    }

    private sealed class FailingEvent : IndunEvent
    {
        public InvalidOperationException Failure { get; } = new("event registration failed");

        protected override void SubscribeCore(WorldInstance worldInstance)
        {
            worldInstance.Events.OnDoodadSpawn += OnDoodadSpawn;
            throw Failure;
        }

        protected override void UnSubscribeCore(WorldInstance worldInstance)
        {
            worldInstance.Events.OnDoodadSpawn -= OnDoodadSpawn;
        }

        private void OnDoodadSpawn(object sender, OnDoodadSpawnArgs args) { }
    }
}
