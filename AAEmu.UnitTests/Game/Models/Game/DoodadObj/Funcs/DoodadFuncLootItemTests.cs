using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.UnitTests.Utils.Mocks;

using AchievementDataBuilder = AAEmu.UnitTests.Game.Models.Game.Char.CharacterAchievementsTests.AchievementDataBuilder;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj.Funcs;

public sealed class DoodadFuncLootItemTests
{
    private const uint LootAchievementId = 1000;
    private static readonly FieldInfo s_achievementsField =
        typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // All nine item-500 rows in both r208022 compacts. See Docs/customized/doodad-currency-rewards.md.
    public static IEnumerable<(uint Id, int Minimum, int Maximum, int Percent)> MoneyRows() =>
    [
        (380, 1, 10, 10000),
        (556, 10, 50, 7000),
        (738, 1000, 10000, 10000),
        (750, 100, 1000, 10000),
        (1017, 1, 10, 10000),
        (1819, 100, 300, 10000),
        (2412, 100, 1000, 10000),
        (2414, 100, 1000, 10000),
        (2415, 100, 1000, 10000)
    ];

    [Test]
    [MethodDataSource(nameof(MoneyRows))]
    public async Task Use_DeployedMoneyRow_GrantsBothInclusiveEndpointsOnce(
        uint id, int minimum, int maximum, int percent)
    {
        foreach (var count in new[] { minimum, maximum })
        {
            var (character, session) = CreateCharacter(123);
            var random = new BoundaryRandom(percent - 1, count);
            var function = CreateFunction(id, minimum, maximum, percent, random);
            var owner = new Doodad();

            function.Use(character, owner, 0);

            await Assert.That(character.Money).IsEqualTo(123L + count);
            await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo((uint)count);
            await Assert.That(MoneyChanges(session)).IsEquivalentTo([count]);
            await Assert.That(random.ChanceBounds).IsEqualTo((0, 10000));
            await Assert.That(random.CountBounds).IsEqualTo(((long)minimum, (long)maximum + 1));
            await Assert.That(owner.ToNextPhase).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(nameof(MoneyRows))]
    public async Task Use_DeployedMoneyRow_ExactWalletCapacitySucceeds(
        uint id, int minimum, int maximum, int percent)
    {
        var (character, session) = CreateCharacter(long.MaxValue - maximum);
        var function = CreateFunction(id, minimum, maximum, percent, new BoundaryRandom(0, maximum));
        var owner = new Doodad();

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo((uint)maximum);
        await Assert.That(MoneyChanges(session)).IsEquivalentTo([maximum]);
        await Assert.That(owner.ToNextPhase).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(MoneyRows))]
    public async Task Use_DeployedMoneyRow_WalletOverflowLeavesBalanceAndProgressUnchanged(
        uint id, int minimum, int maximum, int percent)
    {
        var startingMoney = long.MaxValue - maximum + 1;
        var (character, session) = CreateCharacter(startingMoney);
        var function = CreateFunction(id, minimum, maximum, percent, new BoundaryRandom(0, maximum));
        var owner = new Doodad { ToNextPhase = true };

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(startingMoney);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo(0U);
        await Assert.That(MoneyChanges(session)).IsEmpty();
        await Assert.That(owner.ToNextPhase).IsFalse();
        await Assert.That(session.Packets.Any(packet => Opcode(packet) == SCOffsets.SCErrorMsgPacket)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(MoneyRows))]
    public async Task Use_DeployedMoneyRow_RepeatedSuccessfulActionsEachCreditOnce(
        uint id, int minimum, int maximum, int percent)
    {
        var (character, session) = CreateCharacter(123);
        var random = new BoundaryRandom(0, maximum);
        var function = CreateFunction(id, minimum, maximum, percent, random);
        var owner = new Doodad();

        function.Use(character, owner, 0);
        random.Count = minimum;
        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(123L + maximum + minimum);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo((uint)(maximum + minimum));
        await Assert.That(MoneyChanges(session)).IsEquivalentTo([maximum, minimum]);
        await Assert.That(random.CountCalls).IsEqualTo(2);
    }

    [Test]
    [Arguments(0, 0, false)]
    [Arguments(0, 9999, false)]
    [Arguments(7000, 6999, true)]
    [Arguments(7000, 7000, false)]
    [Arguments(7000, 9999, false)]
    [Arguments(10000, 0, true)]
    [Arguments(10000, 9999, true)]
    public async Task Use_ChanceBoundary_UsesExactlyTheAuthoredNumberOfSuccessfulOutcomes(
        int percent, int chance, bool expectedGrant)
    {
        var (character, session) = CreateCharacter(123);
        var random = new BoundaryRandom(chance, 50);
        var function = CreateFunction(556, 10, 50, percent, random);
        var owner = new Doodad { ToNextPhase = true };

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(expectedGrant ? 173L : 123L);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo(expectedGrant ? 50U : 0U);
        await Assert.That(MoneyChanges(session)).IsEquivalentTo(expectedGrant ? [50] : Array.Empty<int>());
        await Assert.That(random.CountCalls).IsEqualTo(expectedGrant ? 1 : 0);
        await Assert.That(owner.ToNextPhase).IsEqualTo(expectedGrant);
    }

    [Test]
    public async Task Use_ZeroCount_DoesNotCreditOrAdvancePhase()
    {
        var (character, session) = CreateCharacter(123);
        var function = CreateFunction(0, 0, 1, 10000, new BoundaryRandom(0, 0));
        var owner = new Doodad { ToNextPhase = true };

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(123L);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo(0U);
        await Assert.That(session.Packets).IsEmpty();
        await Assert.That(owner.ToNextPhase).IsFalse();
    }

    [Test]
    public async Task Use_Int32MaximumCount_GrantsTheInclusiveEndpointWithoutOverflowingTheRandomBound()
    {
        var (character, session) = CreateCharacter(0);
        var random = new BoundaryRandom(0, int.MaxValue);
        var function = CreateFunction(0, 0, int.MaxValue, 10000, random);
        var owner = new Doodad();

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo((long)int.MaxValue);
        await Assert.That(MoneyChanges(session)).IsEquivalentTo([int.MaxValue]);
        await Assert.That(random.CountBounds).IsEqualTo((0L, (long)int.MaxValue + 1));
        await Assert.That(owner.ToNextPhase).IsTrue();
    }

    [Test]
    [Arguments(-1, 1, 10000)]
    [Arguments(2, 1, 10000)]
    [Arguments(1, 1, -1)]
    [Arguments(1, 1, 10001)]
    public async Task Use_InvalidAuthoredBounds_RejectsBeforeDrawingOrCrediting(int minimum, int maximum, int percent)
    {
        var (character, session) = CreateCharacter(123);
        var random = new BoundaryRandom(0, 1);
        var function = CreateFunction(0, minimum, maximum, percent, random);
        var owner = new Doodad { ToNextPhase = true };

        function.Use(character, owner, 0);

        await Assert.That(character.Money).IsEqualTo(123L);
        await Assert.That(MoneyChanges(session)).IsEmpty();
        await Assert.That(random.ChanceCalls).IsEqualTo(0);
        await Assert.That(random.CountCalls).IsEqualTo(0);
        await Assert.That(owner.ToNextPhase).IsFalse();
    }

    [Test]
    public async Task Use_NonCharacterOrMissingDoodad_GrantsNothing()
    {
        var (character, session) = CreateCharacter(123);
        var random = new BoundaryRandom(0, 1);
        var function = CreateFunction(0, 1, 1, 10000, random);
        var owner = new Doodad { ToNextPhase = true };

        function.Use(new Doodad(), owner, 0);
        function.Use(character, null, 0);

        await Assert.That(character.Money).IsEqualTo(123L);
        await Assert.That(session.Packets).IsEmpty();
        await Assert.That(random.ChanceCalls).IsEqualTo(0);
        await Assert.That(owner.ToNextPhase).IsFalse();
    }

    [Test]
    public async Task Use_ConcurrentCurrencyRewards_DoNotExceedWalletCapacity()
    {
        var (character, session) = CreateCharacter(long.MaxValue - 5);
        var function = new DoodadFuncLootItem
        {
            ItemId = Item.Coins, CountMin = 1, CountMax = 1, Percent = 10000
        };
        var owner = new Doodad();
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            function.Use(character, owner, 0);
        })).ToArray();
        start.Set();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(character.Money).IsEqualTo(long.MaxValue);
        await Assert.That(character.Achievements.GetAmount(LootAchievementId)).IsEqualTo(5U);
        await Assert.That(MoneyChanges(session)).IsEquivalentTo([1, 1, 1, 1, 1]);
    }

    private static DoodadFuncLootItem CreateFunction(uint id, int minimum, int maximum, int percent, Random random)
    {
        return new DoodadFuncLootItem(random)
        {
            Id = id,
            ItemId = Item.Coins,
            CountMin = minimum,
            CountMax = maximum,
            Percent = percent,
            RemainTime = 100000
        };
    }

    private static (Character Character, RecordingSession Session) CreateCharacter(long money)
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.GetLootitem, Item.Coins);
        data.AddAchievement(LootAchievementId, uint.MaxValue, false);
        data.AddObjective(1, LootAchievementId, 100);
        var session = new RecordingSession();
        var character = new CharacterMock { Money = money, Connection = new GameConnection(session) };
        s_achievementsField.SetValue(character, new CharacterAchievements(character, data.Build()));
        return (character, session);
    }

    private static ushort Opcode(byte[] packet)
    {
        return BitConverter.ToUInt16(packet, 6);
    }

    private static int[] MoneyChanges(RecordingSession session)
    {
        return session.Packets.Where(packet => Opcode(packet) == SCOffsets.SCItemTaskSuccessPacket)
            .Select(packet =>
            {
                var stream = new PacketStream(packet[8..]);
                if (stream.ReadByte() != (byte)ItemTaskType.DepositMoney || stream.ReadByte() != 1 ||
                    stream.ReadByte() != (byte)ItemAction.ChangeMoneyAmount)
                    throw new InvalidOperationException("Expected one authoritative wallet change per reward.");
                var amount = stream.ReadInt32();
                if (stream.ReadByte() != 0 || stream.ReadUInt32() != 0 || stream.LeftBytes != 0)
                    throw new InvalidOperationException("Unexpected trailing currency reward data.");
                return amount;
            }).ToArray();
    }

    private sealed class BoundaryRandom(int chance, long count) : Random
    {
        public long Count { get; set; } = count;
        public int ChanceCalls { get; private set; }
        public int CountCalls { get; private set; }
        public (int Minimum, int Maximum) ChanceBounds { get; private set; }
        public (long Minimum, long Maximum) CountBounds { get; private set; }

        public override int Next(int minValue, int maxValue)
        {
            ChanceCalls++;
            ChanceBounds = (minValue, maxValue);
            if (chance < minValue || chance >= maxValue)
                throw new InvalidOperationException("The requested chance endpoint is outside the random bound.");
            return chance;
        }

        public override long NextInt64(long minValue, long maxValue)
        {
            CountCalls++;
            CountBounds = (minValue, maxValue);
            if (Count < minValue || Count >= maxValue)
                throw new InvalidOperationException("The requested count endpoint is outside the random bound.");
            return Count;
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            Packets.Enqueue(packet.ToArray());
        }

        public void AddAttribute(string name, object attribute)
        {
            _attributes.Add(name, attribute);
        }

        public object GetAttribute(string name)
        {
            return _attributes.GetValueOrDefault(name);
        }

        public void ClearAttribute(string name)
        {
            _attributes.Remove(name);
        }

        public void Close()
        {
        }
    }
}
