using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using Mate = AAEmu.Game.Models.Game.Units.Mate;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class PetRepairTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private readonly Dictionary<ulong, Item> _items = [];
    private readonly Dictionary<ulong, MateDb> _saved = [];
    private CharacterMock _owner;
    private WorldInstance _world;
    private Npc _npc;
    private Mock<ISession> _session;
    private int _buffRemovals;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new AccountManager(null, null, TimeProvider.System));
        var skills = new SkillManager(null, null);
        SetField(skills, "_skillModifiers", new Dictionary<uint, List<SkillModifier>>());
        SetField(skills, "_combatBuffs", new Dictionary<uint, List<CombatBuffTemplate>>());
        SetInstance(skills);
        var buffData = new BuffGameData();
        SetField(buffData, "_buffModifiers", new Dictionary<uint, List<BuffModifier>>());
        SetInstance(buffData);
        SetInstance(new MateGameData());
        var items = new ItemManager(Mock.Of<ISkillManager>().Object, Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object, Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object);
        SetInstance(items);
        SetField(items, "_allItems", _items);
        var manager = new WorldManager(null, null, null, null, null);
        SetInstance(manager);
        _world = new WorldInstance(new WorldTemplate
        { Id = 1, CellX = 1, CellY = 1, ZoneKeyByRegions = new uint[16, 16] }, 0, true, 1)
        { Regions = new Region[16, 16] };
        _world.Regions[0, 0] = new Region(_world, 0, 0, 0);
        _world.MateManager = new MateManager(_world);
        SetField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [1] = _world });
        _session = Mock.Of<ISession>();
        var connection = new GameConnection(_session.Object);
        _owner = new CharacterMock
        {
            Id = 7, ObjId = 70, Hp = 100, Money = 100000, ParentWorld = _world,
            NumInventorySlots = 100, NumBankSlots = 100, Connection = connection
        };
        connection.ActiveChar = _owner;
        var containers = new Dictionary<ulong, ItemContainer>();
        foreach (var type in Enum.GetValues<SlotType>())
        {
            if (type == SlotType.EquipmentMate)
                continue;
            var container = new ItemContainer(_owner.Id, type, false, _owner)
                { Owner = _owner, ContainerId = (ulong)containers.Count + 1 };
            containers.Add(container.ContainerId, container);
        }
        SetField(items, "_allPersistentContainers", containers);
        _owner.Inventory = new Inventory(_owner);
        _owner.Mates = new CharacterMates(_owner);
        SetField(_owner.Mates, "_mates", _saved);
        _npc = new Npc { ObjId = 80, ParentWorld = _world, Template = new NpcTemplate { Stabler = true } };
        _world.AddObject(_npc);
        _owner.CurrentInteractionObject = _npc;
        _world.Regions[0, 0].AddObject(_owner);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    [Test]
    public async Task GetPetRepairCost_MatchesAllNativeLevelByteVectors()
    {
        // r208022 39447a40, captured float constants and SSE operation order.
        // These fixed values include levels where double-only arithmetic differs.
        int[] expected =
        [
            0, 2, 6, 14, 26, 43, 65, 91, 123, 161, 205, 254, 309, 371, 440, 515,
            596, 685, 780, 883, 993, 1110, 1234, 1367, 1506, 1654, 1810, 1973, 2144, 2324, 2512, 2708,
            2912, 3125, 3347, 3577, 3815, 4063, 4319, 4585, 4859, 5142, 5434, 5736, 6047, 6367, 6696, 7035,
            7384, 7742, 8109, 8486, 8873, 9270, 9677, 10093, 10519, 10956, 11402, 11859, 12326, 12802, 13290, 13787,
            14295, 14813, 15342, 15881, 16431, 16992, 17563, 18144, 18737, 19340, 19954, 20579, 21215, 21862, 22520, 23189,
            23869, 24560, 25263, 25976, 26701, 27437, 28184, 28943, 29713, 30495, 31288, 32093, 32909, 33737, 34577, 35428,
            36291, 37166, 38052, 38951, 39861, 40783, 41717, 42663, 43621, 44591, 45573, 46567, 47574, 48592, 49623, 50666,
            51722, 52789, 53869, 54962, 56066, 57184, 58313, 59456, 60610, 61778, 62958, 64150, 65355, 66573, 67804, 69048,
            70304, 71573, 72855, 74149, 75457, 76778, 78111, 79458, 80817, 82190, 83576, 84975, 86387, 87812, 89250, 90702,
            92166, 93645, 95136, 96641, 98159, 99690, 101235, 102794, 104365, 105951, 107550, 109162, 110788, 112427, 114081, 115748,
            117428, 119122, 120830, 122552, 124287, 126037, 127800, 129577, 131368, 133173, 134991, 136824, 138671, 140531, 142406, 144295,
            146198, 148115, 150046, 151991, 153950, 155924, 157912, 159914, 161930, 163961, 166006, 168065, 170139, 172227, 174329, 176446,
            178577, 180723, 182883, 185058, 187248, 189452, 191670, 193903, 196151, 198413, 200690, 202982, 205289, 207610, 209946, 212297,
            214662, 217043, 219438, 221848, 224273, 226713, 229168, 231638, 234123, 236623, 239138, 241667, 244212, 246772, 249348, 251938,
            254543, 257164, 259799, 262450, 265117, 267798, 270495, 273207, 275934, 278676, 281434, 284208, 286996, 289800, 292620, 295455,
            298305, 301171, 304052, 306949, 309861, 312789, 315733, 318692, 321667, 324657, 327663, 330685, 333722, 336775, 339844, 342929,
        ];
        for (var level = 0; level <= byte.MaxValue; level++)
            await Assert.That(Character.GetPetRepairCost((byte)level)).IsEqualTo(expected[level]);
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(4)] [Arguments(8)]
    public async Task Request_RejectsEveryOtherBodyLength(int length)
    {
        await Assert.That(CSRepairPetItemsPacket.TryReadRequest(new PacketStream(new byte[length]), out _)).IsFalse();
    }

    [Test]
    public async Task Request_ReadsTheOnlyUnsignedThreeByteNpcId()
    {
        var stream = new PacketStream(new byte[] { 0x56, 0x34, 0x12 });
        await Assert.That(CSRepairPetItemsPacket.TryReadRequest(stream, out var id)).IsTrue();
        await Assert.That(id).IsEqualTo(0x123456u);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task RepairPets_ChargesSeparatelyRoundedBagAndBankCostsOnce()
    {
        var first = AddItem(1, SlotType.Inventory, 1);
        var second = AddItem(2, SlotType.Bank, 1);
        var calls = 0;
        var result = _owner.RepairPets(_npc, () =>
        {
            _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            calls++;
            return true;
        });
        await Assert.That(result).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(99996L);
        await Assert.That(first.DetailInjured || second.DetailInjured).IsFalse();
        await Assert.That(_owner.RepairPets(_npc, () => { calls++; return true; })).IsFalse();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(_owner.Money).IsEqualTo(99996L);
    }

    [Test]
    public async Task RepairPets_ExcludesHealthyItemsAndTradeReservations()
    {
        var healthy = AddItem(1, SlotType.Inventory);
        healthy.DetailInjured = false;
        var reserved = AddItem(2, SlotType.Inventory);
        var repaired = AddItem(3, SlotType.Bank);
        using var reservation = new TradeReservation();
        await Assert.That(reservation.TryReserve(reserved, 1)).IsTrue();
        await Assert.That(_owner.RepairPets(_npc, () => true)).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(99795L);
        await Assert.That(healthy.DetailInjured || repaired.DetailInjured).IsFalse();
        await Assert.That(reserved.DetailInjured).IsTrue();
        await Assert.That(reserved.IsDirty).IsFalse();
    }

    [Test]
    [Arguments("owner")]
    [Arguments("container")]
    [Arguments("slot")]
    [Arguments("empty")]
    [Arguments("saved_owner")]
    public async Task RepairPets_RejectsItemsWithoutCurrentOwnedIdentity(string invalid)
    {
        var item = AddItem(1, SlotType.Inventory);
        switch (invalid)
        {
            case "owner": item.OwnerId = 8; break;
            case "container": item._holdingContainer = _owner.Inventory.Warehouse; break;
            case "slot": item.SlotType = SlotType.Bank; break;
            case "empty": item.Count = 0; break;
            case "saved_owner": _saved[item.Id].Owner = 8; break;
        }
        var called = false;
        await Assert.That(_owner.RepairPets(_npc, () => called = true)).IsFalse();
        await Assert.That(called).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(100000L);
    }

    [Test]
    [Arguments("dead")]
    [Arguments("stabler")]
    [Arguments("interaction")]
    [Arguments("distance")]
    [Arguments("stale")]
    public async Task RepairPets_NeedsLivingOwnerAndCurrentReachableStablemaster(string invalid)
    {
        var item = AddItem(1, SlotType.Inventory);
        switch (invalid)
        {
            case "dead": _owner.Hp = 0; break;
            case "stabler": _npc.Template.Stabler = false; break;
            case "interaction": _owner.CurrentInteractionObject = null; break;
            case "distance": _npc.Transform.Local.SetPosition(0, 0, 5.01f); break;
            case "stale":
                _world.RemoveObject(_npc);
                _world.AddObject(new Npc { ObjId = _npc.ObjId, ParentWorld = _world, Template = _npc.Template });
                break;
        }
        var called = false;
        await Assert.That(_owner.RepairPets(_npc, () => called = true)).IsFalse();
        await Assert.That(called).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(_owner.Money).IsEqualTo(100000L);
    }

    [Test]
    public async Task RepairPets_InclusiveServiceRangeAllowsFiveMetres()
    {
        AddItem(1, SlotType.Inventory);
        _npc.Transform.Local.SetPosition(0, 0, 5);
        await Assert.That(_owner.RepairPets(_npc, () => true)).IsTrue();
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task RepairPets_InsufficientAvailableMoneyDoesNotPrepareRecovery(bool reservedMoney)
    {
        var item = AddItem(1, SlotType.Inventory);
        using var reservation = new TradeReservation();
        if (reservedMoney)
            await Assert.That(reservation.TryReserve(_owner, 100000 - 204)).IsTrue();
        else
            _owner.Money = 204;
        var before = _owner.Money;
        var called = false;
        await Assert.That(_owner.RepairPets(_npc, () => called = true)).IsFalse();
        await Assert.That(called).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(before);
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task RepairPets_ActiveInjuryStateAndPaymentCommitTogether(bool success)
    {
        var item = AddItem(1, SlotType.Inventory);
        var mate = AddActiveMate(item);
        var saved = _saved[item.Id];
        var oldDate = saved.UpdatedAt;
        var sawPrepared = false;
        await Assert.That(_owner.RepairPets(_npc, () =>
        {
            sawPrepared = !item.DetailInjured && !mate.IsInjured && !mate.IsDowned && _owner.Money == 99795;
            _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
            if (_buffRemovals != 0)
                throw new InvalidOperationException("Buffs changed before commit.");
            return success;
        })).IsEqualTo(success);
        await Assert.That(sawPrepared).IsTrue();
        await Assert.That(item.DetailInjured).IsEqualTo(!success);
        await Assert.That(mate.IsInjured && mate.IsDowned).IsEqualTo(!success);
        await Assert.That(item.IsDirty).IsEqualTo(success);
        await Assert.That(_owner.Money).IsEqualTo(success ? 99795L : 100000L);
        await Assert.That(_buffRemovals).IsEqualTo(success ? 2 : 0);
        if (!success)
        {
            await Assert.That(saved.UpdatedAt).IsEqualTo(oldDate);
            await Assert.That(saved.Hp).IsEqualTo(1);
            await Assert.That(saved.Mp).IsEqualTo(37);
        }
    }

    [Test]
    public async Task RepairPets_UnknownCommitRetainsPreparedStateWithoutNotification()
    {
        var item = AddItem(1, SlotType.Inventory);
        var mate = AddActiveMate(item);
        var threw = false;
        try
        {
            _owner.RepairPets(_npc, () => throw new InvalidOperationException("Unknown commit result"));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(item.DetailInjured || mate.IsInjured || mate.IsDowned).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(99795L);
        await Assert.That(_buffRemovals).IsEqualTo(0);
        _session.SendPacket(Any<byte[]>()).WasCalled(Times.Never);
    }

    [Test]
    [Arguments("temporary")]
    [Arguments("owner")]
    [Arguments("saved")]
    public async Task RepairPets_RejectsAnActiveMateWithDifferentPersistentIdentity(string invalid)
    {
        var item = AddItem(1, SlotType.Inventory);
        var mate = AddActiveMate(item);
        switch (invalid)
        {
            case "temporary": mate.IsTemporarySummon = true; break;
            case "owner": mate.OwnerId = 8; break;
            case "saved": mate.DbInfo = new MateDb(); break;
        }
        var called = false;
        await Assert.That(_owner.RepairPets(_npc, () => called = true)).IsFalse();
        await Assert.That(called).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
    }

    [Test]
    public async Task MateItemSync_UsesTheRealLevelAndExperienceBeforeTheSpawnUpdate()
    {
        var item = AddItem(1, SlotType.Inventory, 0);
        item.DetailMateExp = 0;
        var mate = AddActiveMate(item);
        mate.Level = 40;
        mate.Experience = 123456;
        item.IsDirty = false;
        mate.UpdateMateItemData();
        await Assert.That(item.DetailLevel).IsEqualTo((byte)40);
        await Assert.That(item.DetailMateExp).IsEqualTo(123456);
        await Assert.That(item.IsDirty).IsTrue();
    }

    [Test]
    public async Task RepairPets_ThrowingObserverCannotKeepCommittedRecoveryBuffsOrRestorePayment()
    {
        var item = AddItem(1, SlotType.Inventory);
        var mate = AddActiveMate(item);
        var injury = mate.Buffs.GetEffectFromBuffId(Mate.InjuryBuffId);
        var downed = mate.Buffs.GetEffectFromBuffId(Mate.DownedBuffId);
        _session.SendPacket(Any<byte[]>()).Throws(new IOException("Injected packet failure"));
        var threw = false;
        try
        {
            _owner.RepairPets(_npc, () => true);
        }
        catch (IOException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(injury.State).IsEqualTo(EffectState.Finished);
        await Assert.That(downed.State).IsEqualTo(EffectState.Finished);
        await Assert.That(injury.InUse || downed.InUse).IsFalse();
        await Assert.That(mate.Buffs.CheckBuff(Mate.InjuryBuffId)).IsFalse();
        await Assert.That(mate.Buffs.CheckBuff(Mate.DownedBuffId)).IsFalse();
        await Assert.That(item.DetailInjured || mate.IsInjured || mate.IsDowned).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(99795L);
        await Assert.That(_owner.RepairPets(_npc, () => throw new InvalidOperationException("Duplicate charge"))).IsFalse();
        await Assert.That(_owner.Money).IsEqualTo(99795L);
    }

    [Test]
    [Arguments(SlotType.Inventory)]
    [Arguments(SlotType.Bank)]
    public async Task InventorySnapshot_PublishesSavedPetLevelAndExperienceBeforeTheQuote(SlotType type)
    {
        var item = AddItem(1, type, 0);
        var saved = _saved[item.Id];
        saved.Level = 40;
        saved.Xp = 123456;
        var sentLevels = new List<byte>();
        _session.SendPacket(Any<byte[]>()).Callback(() => sentLevels.Add(item.DetailLevel));

        _owner.Inventory.Send();

        await Assert.That(sentLevels.Count > 0).IsTrue();
        await Assert.That(sentLevels.All(level => level == 40)).IsTrue();
        await Assert.That(item.DetailLevel).IsEqualTo((byte)40);
        await Assert.That(item.DetailMateExp).IsEqualTo(123456);
        await Assert.That(item.DetailInjured).IsTrue();
        await Assert.That(item.IsDirty).IsTrue();
        await Assert.That(saved.Hp).IsEqualTo(1);
        await Assert.That(saved.Mp).IsEqualTo(37);
        item.IsDirty = false;
        _owner.Mates.SynchronizeSummonItemDetails();
        await Assert.That(item.IsDirty).IsFalse();
    }

    [Test]
    [Arguments("item_owner")]
    [Arguments("saved_owner")]
    [Arguments("container")]
    [Arguments("slot")]
    [Arguments("zero_level")]
    [Arguments("wide_level")]
    public async Task InventorySnapshot_DoesNotChangeUnownedOrInvalidMateMetadata(string invalid)
    {
        var item = AddItem(1, SlotType.Inventory, 0);
        var saved = _saved[item.Id];
        saved.Level = 40;
        saved.Xp = 123456;
        switch (invalid)
        {
            case "item_owner": item.OwnerId = 8; break;
            case "saved_owner": saved.Owner = 8; break;
            case "container": item._holdingContainer = _owner.Inventory.Warehouse; break;
            case "slot": item.SlotType = SlotType.Bank; break;
            case "zero_level": saved.Level = 0; break;
            case "wide_level": saved.Level = 256; break;
        }
        item.IsDirty = false;

        _owner.Mates.SynchronizeSummonItemDetails();

        await Assert.That(item.DetailLevel).IsEqualTo((byte)0);
        await Assert.That(item.DetailMateExp).IsEqualTo(0);
        await Assert.That(item.IsDirty).IsFalse();
        await Assert.That(item.DetailInjured).IsTrue();
    }

    private SummonMate AddItem(ulong id, SlotType type, byte level = 10)
    {
        var container = type == SlotType.Inventory ? _owner.Inventory.Bag : _owner.Inventory.Warehouse;
        var item = new SummonMate(id, new SummonMateTemplate { Id = 300, MaxCount = 1 }, 1)
        {
            OwnerId = _owner.Id, SlotType = type, Slot = container.Items.Count,
            _holdingContainer = container, DetailInjured = true, DetailLevel = level
        };
        _items.Add(id, item);
        container.Items.Add(item);
        container.UpdateFreeSlotCount();
        _saved.Add(id, new MateDb
        {
            Id = (uint)id, ItemId = id, Owner = _owner.Id, Hp = 1, Mp = 37,
            UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        item.IsDirty = false;
        return item;
    }

    private Mate AddActiveMate(SummonMate item)
    {
        var mate = new Mate
        {
            Id = (uint)item.Id, ObjId = 90, TlId = 10, ParentWorld = _world,
            OwnerId = _owner.Id, OwnerObjId = _owner.ObjId, ItemId = item.Id,
            Hp = 1, Mp = 37, DbInfo = _saved[item.Id], Template = new NpcTemplate { Id = 400 }
        };
        mate.Buffs = new Buffs(mate);
        var effects = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(mate.Buffs)!;
        foreach (var id in new[] { Mate.InjuryBuffId, Mate.DownedBuffId })
        {
            var buff = new Buff(mate, mate, new SkillCasterUnit(mate.ObjId), new BuffTemplate { Id = id }, null, DateTime.UtcNow)
                { Index = id, InUse = true, State = EffectState.Acting };
            buff.Events.OnTimeout += (_, _) => _buffRemovals++;
            effects.Add(buff);
        }
        mate.Region = _world.Regions[0, 0];
        mate.RestoreInjuryState(item, 1, downed: true);
        item.IsDirty = false;
        _world.AddObject(mate);
        _world.MateManager.TrackActiveMate(_owner.Id, mate);
        return mate;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
