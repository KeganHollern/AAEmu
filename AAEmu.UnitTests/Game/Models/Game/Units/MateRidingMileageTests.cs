using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

[NotInParallel]
public sealed class MateRidingMileageTests
{
    private static readonly DateTime Start = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<(FieldInfo Field, object Previous)> _instances = [];
    private WorldInstance _world;
    private Character _owner;
    private Mate _mate;
    private MateDb _saved;
    private SummonMate _item;
    private Mock<ISession> _session;
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { ExpRate = 1 };
        Install(new PermissionManager(Mock.Of<IAccountManager>().Object));
        var worlds = new WorldManager(null, null, null, null, null);
        Install(worlds);
        _world = new WorldInstance(new WorldTemplate { Id = 0, Name = "riding mileage" }, 0, true, 0);
        GC.SuppressFinalize(_world);
        _world.MateManager = new MateManager(_world);
        SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [0] = _world });

        var experience = new ExperienceManager();
        var loader = Mock.Of<IExperienceLevelTemplateLoader>();
        loader.Load().Returns(Enumerable.Range(1, 50).Select(level => new ExperienceLevelTemplate
        {
            Level = (byte)level, TotalExp = (level - 1) * 1000000, TotalMateExp = (level - 1) * 1000000
        }));
        experience.Load(loader.Object, 50, 50);
        Install(experience);

        _session = Mock.Of<ISession>();
        var connection = new GameConnection(_session.Object);
        _owner = new Character(null) { Id = 42, ObjId = 420, Hp = 100, Connection = connection };
        connection.ActiveChar = _owner;
        _owner.Mates = new CharacterMates(_owner);
        SetWorld(_owner, _world);
        SetField(worlds, "_characters", new ConcurrentDictionary<uint, Character> { [_owner.ObjId] = _owner });
        _saved = new MateDb
        {
            Id = 43, ItemId = 44, Owner = _owner.Id, Name = "Riding mount", Level = 10,
            Hp = 100, Mp = 50, Xp = 9000000, Mileage = 100,
            CreatedAt = Start.AddDays(-2), UpdatedAt = Start.AddDays(-1)
        };
        SavedMates().Add(_saved.ItemId, _saved);
        _item = new SummonMate(_saved.ItemId,
            new SummonMateTemplate { Id = 1, MaxCount = 1, FixedGrade = -1 }, 1)
        {
            OwnerId = _owner.Id, DetailLevel = 10, DetailMateExp = _saved.Xp
        };
        var items = new ItemManager(null, null, null, null, null, worlds);
        SetField(items, "_allItems", new Dictionary<ulong, Item> { [_item.Id] = _item });
        Install(items);

        _mate = NewMate();
        _world.MateManager.TrackActiveMate(_owner.Id, _mate);
        AttachDriver();
    }

    [After(Test)]
    public void TearDown()
    {
        if (_owner != null)
            _owner.Transform.Parent = null;
        if (_mate != null)
            _mate.Transform.Parent = null;
        foreach (var (field, previous) in _instances.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _instances.Clear();
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task CurrentDriver_FirstMoveSetsBaselineAndNextMoveUpdatesRuntimeAndSavedMileage()
    {
        await Assert.That(Move(10, 0)).IsEqualTo(0);
        await Assert.That(Move(13, 1)).IsEqualTo(3);
        await Assert.That(_mate.Mileage).IsEqualTo(103);
        await Assert.That(_saved.Mileage).IsEqualTo(103);
        _session.SendPacket(Is<byte[]>(packet => IsMileageDelta(packet, 3))).WasCalled(Times.Once);
    }

    [Test]
    [Arguments("missing-author")]
    [Arguments("foreign-author")]
    [Arguments("owner-id")]
    [Arguments("owner-object")]
    [Arguments("passenger")]
    [Arguments("unmounted")]
    [Arguments("detached")]
    [Arguments("wrong-driver")]
    [Arguments("retired")]
    [Arguments("dead-mate")]
    [Arguments("dead-owner")]
    [Arguments("downed")]
    [Arguments("temporary")]
    [Arguments("zero-item")]
    [Arguments("no-saved-state")]
    [Arguments("saved-owner")]
    [Arguments("saved-id")]
    [Arguments("saved-item")]
    [Arguments("saved-reference")]
    [Arguments("missing-row")]
    [Arguments("other-world")]
    public async Task IneligibleRide_DoesNotChangeMileageOrSendADelta(string reason)
    {
        Move(0, 0);
        var author = _owner;
        switch (reason)
        {
            case "missing-author": author = null; break;
            case "foreign-author": author = new Character(null) { Id = 70, ObjId = 700, Hp = 100 }; break;
            case "owner-id": _mate.OwnerId++; break;
            case "owner-object": _mate.OwnerObjId++; break;
            case "passenger": _owner.AttachedPoint = AttachPointKind.Passenger0; break;
            case "unmounted": _owner.IsRiding = false; break;
            case "detached": _owner.Transform.Parent = null; break;
            case "wrong-driver": _mate.Passengers[AttachPointKind.Driver]._objId++; break;
            case "retired": _mate.AttachmentsRetired = true; break;
            case "dead-mate": _mate.Hp = 0; break;
            case "dead-owner": _owner.Hp = 0; break;
            case "downed": typeof(Mate).GetProperty(nameof(Mate.IsDowned))!.SetValue(_mate, true); break;
            case "temporary": _mate.IsTemporarySummon = true; break;
            case "zero-item": _mate.ItemId = 0; break;
            case "no-saved-state": _mate.DbInfo = null; break;
            case "saved-owner": _saved.Owner++; break;
            case "saved-id": _saved.Id++; break;
            case "saved-item": _saved.ItemId++; break;
            case "saved-reference": _mate.DbInfo = new MateDb
                { Owner = _saved.Owner, Id = _saved.Id, ItemId = _saved.ItemId }; break;
            case "missing-row": SavedMates().Clear(); break;
            case "other-world": SetWorld(_mate, null); break;
        }

        var previous = _mate.Transform.World.Position;
        _mate.Transform.Local.Position = new Vector3(3, 0, 0);
        var delta = _mate.RecordRidingMileage(author, previous, true, false, Start.AddSeconds(1));

        await Assert.That(delta).IsEqualTo(0);
        await Assert.That(_mate.Mileage).IsEqualTo(100);
        await Assert.That(_saved.Mileage).IsEqualTo(100);
        await Assert.That(_mate.Experience).IsEqualTo(9000000);
        await Assert.That(_saved.Xp).IsEqualTo(9000000);
        await Assert.That(_item.DetailMateExp).IsEqualTo(9000000);
        _session.SendPacket(Is<byte[]>(IsMileagePacket)).WasCalled(Times.Never);
        _session.SendPacket(Is<byte[]>(IsExperiencePacket)).WasCalled(Times.Never);
    }

    [Test]
    public async Task ReplacedMate_WithReusedIdsCannotAddMileageToItsSavedState()
    {
        Move(0, 0);
        await Assert.That(_world.MateManager.TryBeginMateRemoval(_owner.Id, _mate)).IsTrue();
        _world.MateManager.CompleteMateRemoval(_mate);
        var replacement = NewMate();
        _world.MateManager.TrackActiveMate(_owner.Id, replacement);

        await Assert.That(Move(3, 1)).IsEqualTo(0);
        await Assert.That(_mate.Mileage).IsEqualTo(100);
        await Assert.That(_saved.Mileage).IsEqualTo(100);
        await Assert.That(_mate.Experience).IsEqualTo(9000000);
        await Assert.That(_saved.Xp).IsEqualTo(9000000);
        await Assert.That(_world.MateManager.GetActiveMateByTlId(_owner.Id, _mate.TlId))
            .IsSameReferenceAs(replacement);
        _session.SendPacket(Is<byte[]>(IsMileagePacket)).WasCalled(Times.Never);
        _session.SendPacket(Is<byte[]>(IsExperiencePacket)).WasCalled(Times.Never);
    }

    [Test]
    [Arguments("carried")]
    [Arguments("changed-frame")]
    [Arguments("skill-controller")]
    public async Task MovementOutsideAnOrdinaryRidingFrame_DoesNotAddMileage(string reason)
    {
        Move(0, 0);
        if (reason == "carried")
            _mate.Transform.Parent = new Mate { Template = new NpcTemplate { Scale = 1 } }.Transform;
        var previous = _mate.Transform.World.Position;
        _mate.Transform.Local.Position = new Vector3(3, 0, 0);

        var delta = _mate.RecordRidingMileage(_owner, previous, reason != "changed-frame",
            reason == "skill-controller", Start.AddSeconds(1));

        await Assert.That(delta).IsEqualTo(0);
        await Assert.That(_mate.Mileage).IsEqualTo(100);
        await Assert.That(_saved.Mileage).IsEqualTo(100);
        await Assert.That(_mate.Experience).IsEqualTo(9000000);
        await Assert.That(_saved.Xp).IsEqualTo(9000000);
        _session.SendPacket(Is<byte[]>(IsMileagePacket)).WasCalled(Times.Never);
        _session.SendPacket(Is<byte[]>(IsExperiencePacket)).WasCalled(Times.Never);
    }

    [Test]
    public async Task NearMaximumMileage_ClampsTheTotalAndSendsOnlyTheAppliedDelta()
    {
        _mate.Mileage = int.MaxValue - 2;
        _saved.Mileage = _mate.Mileage;
        Move(0, 0);

        await Assert.That(Move(5, 1)).IsEqualTo(2);
        await Assert.That(Move(10, 2)).IsEqualTo(0);
        await Assert.That(_mate.Mileage).IsEqualTo(int.MaxValue);
        await Assert.That(_saved.Mileage).IsEqualTo(int.MaxValue);
        _session.SendPacket(Is<byte[]>(packet => IsMileageDelta(packet, 2))).WasCalled(Times.Once);
        _session.SendPacket(Is<byte[]>(IsMileagePacket)).WasCalled(Times.Once);
    }

    [Test]
    public async Task PartialMetres_SendOnlyWholeMileageAndKeepTheFractionAcrossANewRide()
    {
        Move(0, 0);
        await Assert.That(Move(0.75f, 1)).IsEqualTo(0);
        _mate.ResetRidingMovement();
        _owner.Transform.Parent = null;
        _owner.IsRiding = false;
        _owner.AttachedPoint = AttachPointKind.None;
        _mate.Transform.Local.Position = new Vector3(100, 0, 0);
        AttachDriver();

        await Assert.That(Move(100.5f, 2)).IsEqualTo(0);
        await Assert.That(Move(100.75f, 3)).IsEqualTo(1);
        await Assert.That(_mate.Mileage).IsEqualTo(101);
        await Assert.That(_saved.Mileage).IsEqualTo(101);
        _session.SendPacket(Is<byte[]>(packet => IsMileageDelta(packet, 1))).WasCalled(Times.Once);
    }

    [Test]
    public async Task ActiveStateSnapshot_RetainsMileageEarnedByTheCurrentDriver()
    {
        Move(0, 0);
        await Assert.That(Move(3, 1)).IsEqualTo(3);
        _saved.Mileage = 0;

        _owner.Mates.CaptureActiveMateStates(Start.AddMinutes(1));

        await Assert.That(_saved.Mileage).IsEqualTo(103);
        await Assert.That(_saved.UpdatedAt).IsEqualTo(Start.AddMinutes(1));
        await Assert.That(_world.MateManager.GetActiveMateByTlId(_owner.Id, _mate.TlId))
            .IsSameReferenceAs(_mate);
    }

    [Test]
    [Arguments(0.5d, 5)]
    [Arguments(1d, 10)]
    [Arguments(1.5d, 15)]
    [Arguments(2d, 20)]
    public async Task RidingExperience_AppliesTheWorldRateOnceAndUpdatesTheSavedMateAndItem(
        double rate, int expectedExperience)
    {
        AppConfiguration.Instance.World.ExpRate = rate;
        Move(0, 0);

        await Assert.That(Move(20, 1)).IsEqualTo(20);
        await Assert.That(_mate.Experience).IsEqualTo(9000000 + expectedExperience);
        await Assert.That(_saved.Xp).IsEqualTo(_mate.Experience);
        await Assert.That(_item.DetailMateExp).IsEqualTo(_mate.Experience);
        await Assert.That(_saved.Level).IsEqualTo((ushort)10);
        await Assert.That(_item.DetailLevel).IsEqualTo((byte)10);
        await Assert.That(_item.IsDirty).IsTrue();
        _session.SendPacket(Is<byte[]>(packet => IsExperienceDelta(packet, expectedExperience))).WasCalled(Times.Once);
    }

    [Test]
    [Arguments(0.5d, 5)]
    [Arguments(1d, 10)]
    [Arguments(1.5d, 15)]
    [Arguments(2d, 20)]
    public async Task RidingExperience_TheSameRouteGivesTheSameAwardAcrossPacketSizes(
        double rate, int expectedExperience)
    {
        AppConfiguration.Instance.World.ExpRate = rate;
        Move(0, 0);
        Move(20, 1);
        var wholeRouteExperience = _mate.Experience;
        SetProgress(100, 9000000, 10);
        _mate.ResetRidingMovement();
        _mate.Transform.Local.Position = Vector3.Zero;
        Move(0, 0);

        for (var segment = 1; segment <= 40; segment++)
            Move(segment * 0.5f, segment * 0.05);

        await Assert.That(_mate.Mileage).IsEqualTo(120);
        await Assert.That(_mate.Experience).IsEqualTo(wholeRouteExperience);
        await Assert.That(_mate.Experience).IsEqualTo(9000000 + expectedExperience);
        await Assert.That(_saved.Xp).IsEqualTo(_mate.Experience);
        await Assert.That(_item.DetailMateExp).IsEqualTo(_mate.Experience);
    }

    [Test]
    [Arguments((byte)1)]
    [Arguments((byte)10)]
    [Arguments((byte)49)]
    public async Task RidingExperience_DoesNotMultiplyTheAwardByTheMateLevel(byte level)
    {
        var initialExperience = (level - 1) * 1000000;
        SetProgress(100, initialExperience, level);
        Move(0, 0);

        Move(20, 1);

        await Assert.That(_mate.Experience).IsEqualTo(initialExperience + 10);
        await Assert.That(_mate.Level).IsEqualTo(level);
    }

    [Test]
    public async Task RestoredOddMileage_PreservesTheCreditTowardTheNextExperiencePoint()
    {
        SetProgress(101, 9000000, 10);
        Move(0, 0);

        await Assert.That(Move(1, 1)).IsEqualTo(1);

        await Assert.That(_mate.Mileage).IsEqualTo(102);
        await Assert.That(_mate.Experience).IsEqualTo(9000001);
        await Assert.That(_saved.Xp).IsEqualTo(9000001);
        await Assert.That(_item.DetailMateExp).IsEqualTo(9000001);
        _session.SendPacket(Is<byte[]>(packet => IsExperienceDelta(packet, 1))).WasCalled(Times.Once);
    }

    [Test]
    public async Task RestoredMileage_DoesNotAwardExperienceForDistanceFromEarlierRides()
    {
        SetProgress(100, 9000000, 10);
        Move(0, 0);
        await Assert.That(_mate.Experience).IsEqualTo(9000000);

        Move(1, 1);
        await Assert.That(_mate.Experience).IsEqualTo(9000000);
        Move(2, 2);
        await Assert.That(_mate.Experience).IsEqualTo(9000001);
    }

    [Test]
    public async Task RidingExperience_LevelUpUsesTheAuthoredThresholdAndUpdatesTheItemAndSavedLevel()
    {
        SetProgress(100, 9999999, 10);
        var levelUps = 0;
        object levelUpSource = null;
        _owner.Events.OnMateLevelUp += (source, _) =>
        {
            levelUps++;
            levelUpSource = source;
        };
        Move(0, 0);

        Move(2, 1);

        await Assert.That(_mate.Experience).IsEqualTo(10000000);
        await Assert.That(_mate.Level).IsEqualTo((byte)11);
        await Assert.That(_saved.Xp).IsEqualTo(10000000);
        await Assert.That(_saved.Level).IsEqualTo((ushort)11);
        await Assert.That(_item.DetailMateExp).IsEqualTo(10000000);
        await Assert.That(_item.DetailLevel).IsEqualTo((byte)11);
        await Assert.That(levelUps).IsEqualTo(1);
        await Assert.That(levelUpSource).IsSameReferenceAs(_mate);
    }

    [Test]
    public async Task RidingExperience_LevelCapStopsExperienceButRetainsNewMileage()
    {
        SetProgress(100, 48999999, 49);
        Move(0, 0);

        await Assert.That(Move(6, 1)).IsEqualTo(6);
        await Assert.That(_mate.Experience).IsEqualTo(49000000);
        await Assert.That(_mate.Level).IsEqualTo((byte)50);
        await Assert.That(_saved.Xp).IsEqualTo(49000000);
        await Assert.That(_saved.Level).IsEqualTo((ushort)50);
        await Assert.That(_item.DetailMateExp).IsEqualTo(49000000);
        await Assert.That(_item.DetailLevel).IsEqualTo((byte)50);

        await Assert.That(Move(8, 2)).IsEqualTo(2);
        await Assert.That(_mate.Experience).IsEqualTo(49000000);
        await Assert.That(_mate.Mileage).IsEqualTo(108);
        await Assert.That(_saved.Mileage).IsEqualTo(108);
        _session.SendPacket(Is<byte[]>(packet => IsExperienceDelta(packet, 1))).WasCalled(Times.Once);
        _session.SendPacket(Is<byte[]>(IsExperiencePacket)).WasCalled(Times.Once);
    }

    [Test]
    [Arguments(2000000000, 2000000100, 50000)]
    [Arguments(int.MaxValue - 2, int.MaxValue, 1000)]
    public async Task RidingExperience_HighStoredMileageUsesTheDeltaWithoutInt32IntermediateOverflow(
        int previousMileage, int currentMileage, int expectedExperience)
    {
        await Assert.That(Mate.CalculateRidingExperience(previousMileage, currentMileage, 1000))
            .IsEqualTo(expectedExperience);
    }

    private void SetProgress(int mileage, int experience, byte level)
    {
        _mate.Mileage = mileage;
        _saved.Mileage = mileage;
        _mate.Experience = experience;
        _saved.Xp = experience;
        _item.DetailMateExp = experience;
        _mate.Level = level;
        _saved.Level = level;
        _item.DetailLevel = level;
    }

    private Mate NewMate()
    {
        var mate = new Mate
        {
            Id = _saved.Id, ObjId = 430, TlId = 43, OwnerId = _owner.Id, OwnerObjId = _owner.ObjId,
            ItemId = _saved.ItemId, DbInfo = _saved, Name = _saved.Name, Template = new NpcTemplate { Scale = 1 },
            Hp = _saved.Hp, Mp = _saved.Mp, Level = 10, Experience = _saved.Xp, Mileage = _saved.Mileage
        };
        SetWorld(mate, _world);
        return mate;
    }

    private void AttachDriver()
    {
        _mate.Passengers[AttachPointKind.Driver]._objId = _owner.ObjId;
        _owner.Transform.Parent = _mate.Transform;
        _owner.Transform.Local.Position = Vector3.Zero;
        _owner.IsRiding = true;
        _owner.AttachedPoint = AttachPointKind.Driver;
    }

    private int Move(float x, double seconds)
    {
        var previous = _mate.Transform.World.Position;
        _mate.Transform.Local.Position = new Vector3(x, 0, 0);
        return _mate.RecordRidingMileage(_owner, previous, true, false, Start.AddSeconds(seconds));
    }

    private static bool IsMileagePacket(byte[] packet) =>
        packet.Length == 15 && BitConverter.ToUInt16(packet, 6) == SCOffsets.SCMileageChangedPacket;

    private static bool IsMileageDelta(byte[] packet, int delta) =>
        IsMileagePacket(packet) && BitConverter.ToInt32(packet, 11) == delta;

    private static bool IsExperiencePacket(byte[] packet) =>
        packet.Length == 16 && BitConverter.ToUInt16(packet, 6) == SCOffsets.SCExpChangedPacket;

    private static bool IsExperienceDelta(byte[] packet, int delta) =>
        IsExperiencePacket(packet) && BitConverter.ToInt32(packet, 11) == delta && packet[15] == 0;

    private Dictionary<ulong, MateDb> SavedMates() => (Dictionary<ulong, MateDb>)typeof(CharacterMates)
        .GetField("_mates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_owner.Mates)!;

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _instances.Add((field, field.GetValue(null)));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetWorld(GameObject unit, WorldInstance world) =>
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(unit, world);
}
