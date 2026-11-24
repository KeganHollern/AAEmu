using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class ResurrectionRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task RepeatedDeaths_UseAuthoredWaitsAndRetainTheCounterAcrossReload()
    {
        var data = LoadData();
        var character = new CharacterMock { Hp = 0 };
        var waits = new[] { 0, 5000, 45000, 90000, 180000, 180000 };
        for (var index = 0; index < waits.Length; index++)
        {
            var now = Now.AddSeconds(index * 200);
            character.ComputeDeathWaitTime(now, KillReason.Damage, data);
            await Assert.That(character.RezWaitDuration).IsEqualTo(waits[index]);
            await Assert.That(character.RezPenaltyDuration).IsEqualTo(600000);
            await Assert.That(character.GetRemainingResurrectionWait(now.AddMilliseconds(waits[index]).AddTicks(-1))).IsEqualTo(1);
            await Assert.That(character.CanResurrect(now.AddMilliseconds(waits[index] - 1))).IsFalse();
            await Assert.That(character.CanResurrect(now.AddMilliseconds(waits[index]))).IsTrue();
            // These are the persisted fields used by both character load paths.
            character = new CharacterMock
            {
                Hp = 0, DeadCount = character.DeadCount, DeadTime = character.DeadTime,
                RezWaitDuration = character.RezWaitDuration, RezPenaltyDuration = character.RezPenaltyDuration
            };
        }
        await Assert.That(character.DeadCount).IsEqualTo((short)5);
        character.ComputeDeathWaitTime(character.DeadTime.AddSeconds(600), KillReason.Damage, data);
        await Assert.That(character.RezWaitDuration).IsEqualTo(0);
        await Assert.That(character.DeadCount).IsEqualTo((short)1);
    }

    [Test]
    [Arguments(0, 83, 97_000)]
    [Arguments(0, 181, 0)]
    [Arguments(1, 83, 0)]
    public async Task Reconnect_CharacterStateWritesOnlyTheOutstandingManualWait(int hp, int secondsSinceDeath, int expected)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            Id = 7, Name = "Temple", Faction = new(), FactionName = "Nuia", Hp = hp,
            DeadCount = 5, RezWaitDuration = 180_000, RezPenaltyDuration = 600_000
        };
        character.Mails = new CharacterMails(character, null);
        var items = new ItemManager(null, null, null, null, null, null);
        var instance = typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = instance.GetValue(null);
        try
        {
            instance.SetValue(null, items);
            CreateInventory(character, items);
            character.Equipment.ContainerSize = 28;
            var start = DateTime.UtcNow;
            character.DeadTime = start.AddSeconds(-secondsSinceDeath);
            var stream = new SCCharacterStatePacket(character).Write(new PacketStream());
            var elapsed = (int)(DateTime.UtcNow - start).TotalMilliseconds;

            await Assert.That(stream.ReadUInt32()).IsEqualTo(character.Transform.InstanceId);
            await Assert.That(new Guid(stream.ReadBytes())).IsEqualTo(character.Guid);
            await Assert.That(stream.ReadInt32()).IsBetween(Math.Max(0, expected - elapsed), expected);
            var persistedCharacter = character.Write(new PacketStream()).GetBytes();
            await Assert.That(stream.ReadBytes(persistedCharacter.Length).SequenceEqual(persistedCharacter)).IsTrue();
        }
        finally
        {
            instance.SetValue(null, previous);
        }
    }

    [Test]
    public async Task SiegeDeath_UsesTheAuthoredSiegeColumn()
    {
        var data = LoadData();
        var character = new CharacterMock { Hp = 0 };
        foreach (var expected in new[] { 20000, 15000, 10000, 5000, 0 })
        {
            character.ComputeDeathWaitTime(character.DeadTime == default ? Now : character.DeadTime.AddSeconds(100),
                KillReason.PvpSiege, data);
            await Assert.That(character.RezWaitDuration).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task EarlyRequest_DoesNotConsumeTheOfferAndBoundaryRequestSucceedsOnce()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now, RezWaitDuration = 45000 };
        character.OfferResurrection(new SkillCasterUnit(2), 50, 25, offeredAt: Now);
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(44999), out _)).IsFalse();
        await Assert.That(character.RezTime).IsEqualTo(default(DateTime));
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(45000), out var offer)).IsTrue();
        await Assert.That(offer.GetHealth(100)).IsEqualTo(50);
        await Assert.That(character.RezTime).IsEqualTo(Now.AddMilliseconds(45000));
        await Assert.That(character.TryBeginResurrection(true, Now.AddMilliseconds(45000), out _)).IsFalse();
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task LivingCharacter_CannotStartEitherRevivalPath(bool inPlace)
    {
        var character = new CharacterMock { Hp = 1, DeadTime = Now.AddMinutes(-10) };
        await Assert.That(character.TryBeginResurrection(inPlace, Now, out _)).IsFalse();
        await Assert.That(character.RezTime).IsEqualTo(default(DateTime));
    }

    [Test]
    public async Task ExpiredOrPreviousDeathOffers_CannotAuthorizeInPlaceRevival()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now };
        await Assert.That(character.TryBeginResurrection(true, Now, out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(2), 50, 50, offeredAt: Now);
        await Assert.That(character.TryBeginResurrection(true, Now + Character.ResurrectionOfferLifetime, out _)).IsFalse();
        character.OfferResurrection(new SkillCasterUnit(2), 50, 50, offeredAt: Now);
        character.DeadTime = Now.AddSeconds(1);
        await Assert.That(character.TryBeginResurrection(true, Now.AddSeconds(1), out _)).IsFalse();
        await Assert.That(character.TryBeginResurrection(false, Now.AddSeconds(1), out _)).IsTrue();
    }

    [Test]
    public async Task ReplacementOffer_GetsItsOwnExpiryAndDoesNotRetainExpiredPriestExperience()
    {
        var character = new CharacterMock { Hp = 0, DeadTime = Now };
        character.OfferResurrection(new SkillCasterUnit(2), 10, 10, restoreExperience: 500, offeredAt: Now);
        var replacementAt = Now + Character.ResurrectionOfferLifetime;
        character.OfferResurrection(new SkillCasterUnit(3), 50, 50, offeredAt: replacementAt);
        await Assert.That(character.TryTakeResurrectionOffer(out var offer, replacementAt.AddSeconds(1))).IsTrue();
        await Assert.That(offer.RestoreExperience).IsEqualTo(0);
        await Assert.That(offer.ExpiresAt).IsEqualTo(replacementAt + Character.ResurrectionOfferLifetime);
    }

    [Test]
    [Arguments(true, (byte)2, 0, (byte)0, false)]
    [Arguments(false, (byte)2, 25, (byte)7, false)]
    [Arguments(true, (byte)1, 0, (byte)0, true)]
    public async Task DoDie_NpcDeathPenaltyFlagControlsLossesAndPreservesResurrectionRules(
        bool noPenalty, byte level, int expectedExpLoss, byte expectedDurabilityLoss, bool expectedFreeOffer)
    {
        var previousSingletons = new List<(FieldInfo Field, object Value)>();
        var configuration = AppConfiguration.Instance;
        var previousWorldConfig = configuration.World;
        var previousDebugInfo = configuration.DebugInfo;
        var world = new WorldInstance(new WorldTemplate { Name = "death-test" }, 0, true, WorldManager.DefaultInstanceId);
        GC.SuppressFinalize(world);
        try
        {
            configuration.World = new WorldConfig { MinimumExpLossLevel = 2, ExpLossRateAtDeath = 0.25f };
            configuration.DebugInfo = false;
            world.MateManager = new MateManager(world);
            var worlds = new WorldManager(null, null, null, null, null);
            SetField(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
            Install(worlds);
            var character = new CharacterMock
            {
                Id = 1, ObjId = 1, Hp = 0, Level = level, ParentWorld = world,
                DeadCount = 1, DeadTime = DateTime.UtcNow.AddSeconds(-1), RezPenaltyDuration = 600000
            };
            var startingExperience = level == 2 ? 150 : 50;
            SetField<Character>(character, "<Experience>k__BackingField", startingExperience);
            var gear = new DeathTestGear { Durability = 100, Slot = (int)EquipmentItemSlot.Chest };

            var loader = Mock.Of<IExperienceLevelTemplateLoader>();
            loader.Load().Returns([
                new ExperienceLevelTemplate { Level = 1, TotalExp = 0 },
                new ExperienceLevelTemplate { Level = 2, TotalExp = 100 },
                new ExperienceLevelTemplate { Level = 3, TotalExp = 200 }
            ]);
            var experience = new ExperienceManager();
            experience.Load(loader.Object, 3, 3);
            Install(experience);
            Install(LoadData());
            Install(new PriestBuffGameData());
            Install(new DuelManager());
            var zones = new ZoneManager(null, null);
            SetField(zones, "_zones", new Dictionary<uint, Zone>());
            Install(zones);
            var skills = new SkillManager(null, null);
            SetField(skills, "_taggedBuffs", new Dictionary<uint, List<uint>>());
            Install(skills);
            var items = new ItemManager(null, null, null, null, null, null);
            SetField(items, "_config", new ItemConfig { DeathDurabilityLossRatio = 7 });
            Install(items);
            CreateInventory(character, items);
            character.Equipment.Items.Add(gear);
            var npc = new DeathTestNpc
            {
                ObjId = 2, ParentWorld = world, Template = new NpcTemplate { NoPenalty = noPenalty, NoExp = true }
            };

            character.DoDie(npc, KillReason.Damage);

            await Assert.That(character.Experience).IsEqualTo(startingExperience - expectedExpLoss);
            await Assert.That(character.LastExpLoss).IsEqualTo(expectedExpLoss);
            await Assert.That(character.RecoverableExp).IsEqualTo(expectedExpLoss * 4 / 5);
            await Assert.That(gear.Durability).IsEqualTo((byte)(100 - expectedDurabilityLoss));
            await Assert.That(character.RezWaitDuration).IsEqualTo(5000);
            await Assert.That(character.TryTakeResurrectionOffer(out _)).IsEqualTo(expectedFreeOffer);
        }
        finally
        {
            configuration.World = previousWorldConfig;
            configuration.DebugInfo = previousDebugInfo;
            foreach (var (field, previous) in previousSingletons)
                field.SetValue(null, previous);
        }

        void Install<T>(T singleton) where T : class
        {
            var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
            previousSingletons.Add((field, field.GetValue(null)));
            field.SetValue(null, singleton);
        }
    }

    [Test]
    [Arguments(0)] [Arguments(2)] [Arguments(3)]
    public async Task Packet_RejectsTruncatedOrTrailingBytes(int count)
    {
        await Assert.That(CSResurrectCharacterPacket.TryReadRequest(new PacketStream(new byte[count]), out _)).IsFalse();
    }

    [Test]
    [Arguments((byte)0, true)] [Arguments((byte)1, true)] [Arguments((byte)2, false)] [Arguments((byte)255, false)]
    public async Task Packet_UsesExactlyOneBooleanByte(byte value, bool accepted)
    {
        var stream = new PacketStream(new[] { value });
        await Assert.That(CSResurrectCharacterPacket.TryReadRequest(stream, out var inPlace)).IsEqualTo(accepted);
        if (accepted)
        {
            await Assert.That(inPlace).IsEqualTo(value == 1);
            await Assert.That(stream.Pos).IsEqualTo(stream.Count);
        }
    }

    private static void SetField<T>(T target, string field, object value) =>
        typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void CreateInventory(Character character, ItemManager items)
    {
        var containers = Enum.GetValues<SlotType>().Where(type => type != SlotType.EquipmentMate)
            .Select((type, index) => new ItemContainer(character.Id, type, false, character) { ContainerId = (ulong)index + 1 })
            .ToDictionary(container => container.ContainerId);
        SetField(items, "_allPersistentContainers", containers);
        character.Inventory = new Inventory(character);
    }

    private sealed class DeathTestGear : EquipItem
    {
        public override byte MaxDurability => 100;
    }

    private sealed class DeathTestNpc : Npc
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private static ResurrectionGameData LoadData()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE resurrection_waiting_times(id INTEGER, penalty_duration INTEGER, waiting_time INTEGER, siege_waiting_time INTEGER);
            INSERT INTO resurrection_waiting_times VALUES(5,600,180,0),(1,600,0,20),(3,600,45,10),(2,600,5,15),(4,600,90,5);
            """;
        command.ExecuteNonQuery();
        var data = new ResurrectionGameData();
        data.Load(connection);
        return data;
    }
}
