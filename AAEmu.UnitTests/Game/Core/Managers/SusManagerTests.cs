using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public class SusManagerTests
{
    private static readonly FieldInfo s_worldManagerInstance = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
    private object _previousWorldManager;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = s_worldManagerInstance.GetValue(null);
        var worlds = new WorldManager(null, null, null, null, null);
        _world = new WorldInstance(new WorldTemplate { Id = 0, Name = "Movement telemetry test" }, 0, true, 0);
        GC.SuppressFinalize(_world);
        typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(worlds, new ConcurrentDictionary<uint, WorldInstance> { [0] = _world });
        s_worldManagerInstance.SetValue(null, worlds);
    }

    [After(Test)]
    public void TearDown() => s_worldManagerInstance.SetValue(null, _previousWorldManager);

    [Test]
    public async Task Constructor_WithMockedWorldManager_DoesNotThrow()
    {
        var mockWorldManager = Mock.Of<IWorldManager>();

        using var manager = new SusManager(mockWorldManager.Object);

        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockWorldManager);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnalyzeMovement_ConcurrentSamples_ReportFastMovementOnce(bool isMate)
    {
        using var manager = CreateManager();
        var unit = AddUnit(isMate, 100);
        Parallel.For(0, 64, _ => Analyze(manager, unit));
        unit.Transform.Local.SetPosition(300, 0, 0);
        Parallel.For(0, 64, _ => Analyze(manager, unit));

        await Assert.That(manager.PendingCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnalyzeMovement_TeleportWithInFlightSamples_UsesNewPosition(bool isMate)
    {
        using var manager = CreateManager();
        var unit = AddUnit(isMate, 100);
        Analyze(manager, unit);

        unit.DisabledSetPosition = true;
        Parallel.For(0, 64, _ => Analyze(manager, unit));
        unit.Transform.Local.SetPosition(1000, 0, 0);
        unit.DisabledSetPosition = false;
        Analyze(manager, unit);

        await Assert.That(manager.PendingCount).IsEqualTo(0);
        unit.Transform.Local.SetPosition(1200, 0, 0);
        Analyze(manager, unit);
        await Assert.That(manager.PendingCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnalyzeMovement_RemovedUnit_CannotReseedBeforeRejoining(bool isMate)
    {
        using var manager = CreateManager();
        var unit = AddUnit(isMate, 100);
        Analyze(manager, unit);

        await Assert.That(_world.RemoveObject(unit)).IsTrue();
        Analyze(manager, unit);
        unit.Transform.Local.SetPosition(1000, 0, 0);
        _world.AddObject(unit);
        Analyze(manager, unit);

        await Assert.That(manager.PendingCount).IsEqualTo(0);
        unit.Transform.Local.SetPosition(1200, 0, 0);
        Analyze(manager, unit);
        await Assert.That(manager.PendingCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnalyzeMovement_ReplacedUnit_CannotChangeNewUnitSample(bool isMate)
    {
        using var manager = CreateManager();
        var previous = AddUnit(isMate, 100);
        Analyze(manager, previous);
        await Assert.That(_world.RemoveObject(previous)).IsTrue();

        var replacement = AddUnit(isMate, 1000);
        Analyze(manager, replacement);
        Analyze(manager, previous);
        replacement.Transform.Local.SetPosition(1001, 0, 0);
        Analyze(manager, replacement);

        await Assert.That(manager.PendingCount).IsEqualTo(0);
        replacement.Transform.Local.SetPosition(1201, 0, 0);
        Analyze(manager, replacement);
        await Assert.That(manager.PendingCount).IsEqualTo(1);
    }

    private Unit AddUnit(bool isMate, float x)
    {
        Unit unit = isMate ? new Mate() : new Character(null);
        unit.Id = 42;
        unit.ObjId = 100;
        unit.ParentWorld = _world;
        unit.Transform.Local.SetPosition(x, 0, 0);
        _world.AddObject(unit);
        return unit;
    }

    private static SusManager CreateManager() => new(Mock.Of<IWorldManager>().Object) { WriteBatch = _ => { } };

    private static void Analyze(SusManager manager, Unit unit)
    {
        if (unit is Character player)
            manager.AnalyzePlayerDeltaMovement(player, 5f);
        else
            manager.AnalyzeMountDeltaMovement((Mate)unit, 5f);
    }
}
