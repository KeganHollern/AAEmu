using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Achievement.Enums;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using System.Net;
using System.Net.Sockets;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterAchievementsTests
{
    [Test]
    public async Task Reconcile_LevelFiftyFive_CompletesLeafChainsAndMetaAchievements()
    {
        using var data = new AchievementDataBuilder();
        AddLevelAchievements(data);
        var gameData = data.Build();
        var completedAt = new DateTimeOffset(2026, 8, 29, 15, 0, 0, TimeSpan.Zero);
        var character = new CharacterMock { Level = 55 };
        var achievements = new CharacterAchievements(
            character,
            gameData,
            new FakeTimeProvider(completedAt),
            () => true);

        achievements.ReconcileAuthoritativeState();

        uint[] expectedCompleted = [1, 2, 3, 4, 5, 6, 7, 1478, 1479, 1480, 1481, 1482, 1483];
        foreach (var achievementId in expectedCompleted)
        {
            await Assert.That(achievements.IsCompleted(achievementId)).IsTrue();
            await Assert.That(achievements.GetCompletionTime(achievementId)).IsEqualTo(completedAt.UtcDateTime);
        }

        await Assert.That(achievements.GetAmount(2)).IsEqualTo(5u);
        await Assert.That(achievements.GetAmount(7)).IsEqualTo(50u);
        await Assert.That(achievements.GetAmount(1483)).IsEqualTo(55u);
        await Assert.That(achievements.GetAmount(1)).IsEqualTo(6u);
        await Assert.That(achievements.GetAmount(1478)).IsEqualTo(5u);

        var packets = achievements.CreateSnapshotPackets();
        await Assert.That(packets.Count).IsEqualTo(1);
        var stream = packets[0].Write(new PacketStream());
        stream.Rollback();
        await Assert.That(stream.ReadInt32()).IsEqualTo(expectedCompleted.Length);
        var expectedAmounts = new Dictionary<uint, uint>
        {
            [1] = 6,
            [2] = 5,
            [3] = 10,
            [4] = 20,
            [5] = 30,
            [6] = 40,
            [7] = 50,
            [1478] = 5,
            [1479] = 51,
            [1480] = 52,
            [1481] = 53,
            [1482] = 54,
            [1483] = 55
        };
        foreach (var achievementId in expectedCompleted)
        {
            await Assert.That(stream.ReadUInt32()).IsEqualTo(achievementId);
            await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedAmounts[achievementId]);
            await Assert.That(stream.ReadInt64()).IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));
        }
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateMaximum_PersistenceFailure_HidesCompletionAndRetriesInPacketOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);
        var completedAt = new DateTimeOffset(2026, 8, 29, 17, 0, 0, TimeSpan.Zero);
        var persistenceSucceeds = false;
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(completedAt),
            () => persistenceSucceeds);

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementChangedPacket);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 8)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToInt32(session.Packets[0], 12)).IsEqualTo(1);

        var pendingSnapshot = achievements.CreateSnapshotPackets().Single().Write(new PacketStream());
        pendingSnapshot.Rollback();
        await Assert.That(pendingSnapshot.ReadInt32()).IsEqualTo(1);
        await Assert.That(pendingSnapshot.ReadUInt32()).IsEqualTo(1000u);
        await Assert.That(pendingSnapshot.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(pendingSnapshot.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));

        persistenceSucceeds = true;
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(2);
        AssertPacketOpcode(session.Packets[1], SCOffsets.SCAchievementCompletedPacket);
        await Assert.That(BitConverter.ToUInt32(session.Packets[1], 8)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToInt64(session.Packets[1], 12))
            .IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        await Assert.That(session.Packets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SendSnapshot_PendingCompletion_RetriesPersistenceBeforeItShowsCompletion()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false);
        data.AddObjective(1, 1000, 100);
        var completedAt = new DateTimeOffset(2026, 8, 29, 17, 30, 0, TimeSpan.Zero);
        var persistenceSucceeds = false;
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(completedAt),
            () => persistenceSucceeds);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        session.Packets.Clear();

        achievements.SendSnapshot();

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementsPacket);
        await Assert.That(BitConverter.ToInt32(session.Packets[0], 8)).IsEqualTo(1);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 12)).IsEqualTo(1000u);
        await Assert.That(BitConverter.ToUInt32(session.Packets[0], 16)).IsEqualTo(1u);
        await Assert.That(BitConverter.ToInt64(session.Packets[0], 20))
            .IsEqualTo(Helpers.UnixTime(DateTime.MinValue));

        persistenceSucceeds = true;
        session.Packets.Clear();
        achievements.SendSnapshot();

        await Assert.That(session.Packets.Count).IsEqualTo(1);
        AssertPacketOpcode(session.Packets[0], SCOffsets.SCAchievementsPacket);
        await Assert.That(BitConverter.ToInt64(session.Packets[0], 20))
            .IsEqualTo(Helpers.UnixTime(completedAt.UtcDateTime));
    }

    [Test]
    public async Task UpdateMaximum_LeafAndMetaCompletion_PreservesDependencyPacketOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1483, 1, false);
        data.AddObjective(1, 1483, 100);
        data.AddRecord(101, CharRecordKind.CompleteAchievement, 1483);
        data.AddAchievement(1478, 1, false);
        data.AddObjective(2, 1478, 101);
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 18, 30, 0, TimeSpan.Zero)),
            () => true);

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(session.Packets.Count).IsEqualTo(4);
        ushort[] expectedOpcodes =
        [
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementCompletedPacket,
            SCOffsets.SCAchievementCompletedPacket
        ];
        uint[] expectedIds = [1483, 1478, 1483, 1478];
        for (var index = 0; index < session.Packets.Count; index++)
        {
            AssertPacketOpcode(session.Packets[index], expectedOpcodes[index]);
            await Assert.That(BitConverter.ToUInt32(session.Packets[index], 8)).IsEqualTo(expectedIds[index]);
        }
    }

    [Test]
    public async Task Increment_AddsAndSaturatesAtUIntMaximum()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, uint.MaxValue, false);
        data.AddObjective(1, 1000, 100);
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.Increment(CharRecordKind.KillNpc, 1, 0, 0);
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(0u);

        achievements.Increment(CharRecordKind.KillNpc, 1, 0, uint.MaxValue - 2);
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(uint.MaxValue - 2);

        achievements.Increment(CharRecordKind.KillNpc, 1, 0, 10);
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(uint.MaxValue);

        achievements.Increment(CharRecordKind.KillNpc, 1, 0);
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(uint.MaxValue);
    }

    [Test]
    public async Task Increment_Value2Wildcard_RequiresOptInAndKeepsSelectorsIndependent()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.GetItemType, 500, 3);
        data.AddRecord(101, CharRecordKind.GetItemType, 500, uint.MaxValue);
        data.AddRecord(102, CharRecordKind.GetItemType, 500, 4);
        data.AddAchievement(1000, 100, false);
        data.AddObjective(1, 1000, 100);
        data.AddAchievement(1001, 100, false);
        data.AddObjective(2, 1001, 101);
        data.AddAchievement(1002, 100, false);
        data.AddObjective(3, 1002, 102);
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.Increment(CharRecordKind.GetItemType, 500, 3, 2);

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(2u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(0u);
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(0u);

        achievements.Increment(CharRecordKind.GetItemType, 500, 3, 3, true);

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(5u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(3u);
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(0u);
    }

    [Test]
    public async Task Increment_ItemAndCraftGrades_UpdateExactAndWildcardSelectors()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.GetItemType, 500, 3);
        data.AddRecord(101, CharRecordKind.GetItemType, 500, uint.MaxValue);
        data.AddRecord(102, CharRecordKind.GetItemType, 500, 4);
        data.AddRecord(103, CharRecordKind.MakeItemType, 600, 5);
        data.AddRecord(104, CharRecordKind.MakeItemType, 600, uint.MaxValue);
        data.AddRecord(105, CharRecordKind.MakeItemType, 600, 6);
        data.AddRecord(106, CharRecordKind.MakeItemImpl, 21, 2);
        data.AddRecord(107, CharRecordKind.MakeItemImpl, 21, uint.MaxValue);
        data.AddRecord(108, CharRecordKind.MakeItemImpl, 21, 3);
        for (uint index = 0; index < 9; index++)
        {
            data.AddAchievement(1000 + index, 100, false);
            data.AddObjective(1 + index, 1000 + index, 100 + index);
        }
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.Increment([
            new AchievementProgressEvent(CharRecordKind.GetItemType, 500, 3, 2, true),
            new AchievementProgressEvent(CharRecordKind.MakeItemType, 600, 5, 3, true),
            new AchievementProgressEvent(CharRecordKind.MakeItemImpl, 21, 2, 4, true)
        ]);

        uint[] expectedAmounts = [2, 2, 0, 3, 3, 0, 4, 4, 0];
        for (uint index = 0; index < expectedAmounts.Length; index++)
        {
            await Assert.That(achievements.GetAmount(1000 + index))
                .IsEqualTo(expectedAmounts[index]);
        }
    }

    [Test]
    public async Task Increment_BatchEvaluatesFinalStateOnceAndPreservesPacketOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.DeadByPvp);
        data.AddRecord(101, CharRecordKind.DeadByPvp, 14);
        data.AddRecord(102, CharRecordKind.DeadByPvp, 15);
        data.AddAchievement(1000, 2, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1000, 101);
        data.AddAchievement(1001, 1, false);
        data.AddObjective(3, 1001, 100);
        data.AddAchievement(1002, 1, false);
        data.AddObjective(4, 1002, 102);
        var session = new RecordingSession();
        var character = new CharacterMock { Connection = new GameConnection(session) };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)),
            () => true);

        achievements.Increment([
            new AchievementProgressEvent(CharRecordKind.DeadByPvp, 14, 0, 1),
            new AchievementProgressEvent(CharRecordKind.DeadByPvp, 0, 0, 1)
        ]);

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(2u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(1u);
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(0u);
        await Assert.That(session.Packets.Count).IsEqualTo(4);

        ushort[] expectedOpcodes =
        [
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementChangedPacket,
            SCOffsets.SCAchievementCompletedPacket,
            SCOffsets.SCAchievementCompletedPacket
        ];
        uint[] expectedIds = [1000, 1001, 1000, 1001];
        for (var index = 0; index < session.Packets.Count; index++)
        {
            AssertPacketOpcode(session.Packets[index], expectedOpcodes[index]);
            await Assert.That(BitConverter.ToUInt32(session.Packets[index], 8)).IsEqualTo(expectedIds[index]);
        }

        await Assert.That(BitConverter.ToInt32(session.Packets[0], 12)).IsEqualTo(2);
        await Assert.That(BitConverter.ToInt32(session.Packets[1], 12)).IsEqualTo(1);
    }

    [Test]
    public async Task RewardDelivery_WaitsForCompletionPersistence()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false, 34138);
        data.AddObjective(1, 1000, 100);
        var persistenceSucceeds = false;
        var deliveries = 0;
        var achievements = new CharacterAchievements(
            new CharacterMock(),
            data.Build(),
            TimeProvider.System,
            () => persistenceSucceeds,
            (_, _) =>
            {
                deliveries++;
                return AchievementRewardStatus.Inventory;
            });

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(deliveries).IsEqualTo(0);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Pending);

        persistenceSucceeds = true;
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(deliveries).IsEqualTo(1);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Inventory);
    }

    [Test]
    public async Task RewardDelivery_FailureStaysPendingThenRetriesOnce()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false, 34138);
        data.AddObjective(1, 1000, 100);
        var attempts = 0;
        var achievements = new CharacterAchievements(
            new CharacterMock(),
            data.Build(),
            TimeProvider.System,
            () => true,
            (achievementId, itemTemplateId) =>
            {
                attempts++;
                return attempts == 1 ? null : AchievementRewardStatus.Inventory;
            });

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Pending);

        achievements.SendSnapshot();
        achievements.SendSnapshot();

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Inventory);
    }

    [Test]
    [Arguments(AchievementRewardStatus.Inventory)]
    [Arguments(AchievementRewardStatus.Mail)]
    public async Task RewardDelivery_TerminalStatusPreventsDuplicateAttempts(
        AchievementRewardStatus terminalStatus)
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false, 34138);
        data.AddObjective(1, 1000, 100);
        var attempts = 0;
        var achievements = new CharacterAchievements(
            new CharacterMock(),
            data.Build(),
            TimeProvider.System,
            () => true,
            (_, _) =>
            {
                attempts++;
                return terminalStatus;
            });

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        achievements.SendSnapshot();
        achievements.Increment(CharRecordKind.KillNpc, 1, 0);

        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(terminalStatus);
    }

    [Test]
    public async Task RewardDelivery_FailureRetriesOnNextUnchangedProgressEvent()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false, 34138);
        data.AddObjective(1, 1000, 100);
        var attempts = 0;
        var achievements = new CharacterAchievements(
            new CharacterMock(),
            data.Build(),
            TimeProvider.System,
            () => true,
            (_, _) => ++attempts == 1 ? null : AchievementRewardStatus.Inventory);

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Inventory);
    }

    [Test]
    public async Task RewardDelivery_LeafAndMetaRewardsUseAchievementOrderAndMailStatus()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddAchievement(1000, 1, false, 34138);
        data.AddObjective(1, 1000, 100);
        data.AddRecord(101, CharRecordKind.CompleteAchievement, 1000);
        data.AddAchievement(1001, 1, false, 32750);
        data.AddObjective(2, 1001, 101);
        List<(uint AchievementId, uint ItemTemplateId)> deliveries = [];
        var achievements = new CharacterAchievements(
            new CharacterMock(),
            data.Build(),
            TimeProvider.System,
            () => true,
            (achievementId, itemTemplateId) =>
            {
                deliveries.Add((achievementId, itemTemplateId));
                return AchievementRewardStatus.Mail;
            });

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 1);

        await Assert.That(deliveries.Count).IsEqualTo(2);
        await Assert.That(deliveries[0]).IsEqualTo((1000u, 34138u));
        await Assert.That(deliveries[1]).IsEqualTo((1001u, 32750u));
        await Assert.That(achievements.GetRewardStatus(1000)).IsEqualTo(AchievementRewardStatus.Mail);
        await Assert.That(achievements.GetRewardStatus(1001)).IsEqualTo(AchievementRewardStatus.Mail);
    }

    [Test]
    public async Task UpdateMaximum_ObjectiveRulesAndPrerequisite_CompleteInOrder()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        data.AddRecord(101, CharRecordKind.KillNpc, 2);
        data.AddRecord(102, CharRecordKind.KillNpc, 3);
        data.AddAchievement(1000, 5, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1000, 101);
        data.AddAchievement(1001, 2, true);
        data.AddObjective(3, 1001, 100);
        data.AddObjective(4, 1001, 101);
        data.AddObjective(5, 1001, 102);
        data.AddAchievement(1002, 0, true);
        data.AddObjective(6, 1002, 100);
        data.AddObjective(7, 1002, 101);
        data.AddObjective(8, 1002, 102);
        data.AddAchievement(1003, 1, false);
        data.AddObjective(9, 1003, 102);
        data.AddPrerequisite(1, 1003, 1002);
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 1, 0, 2);
        achievements.UpdateMaximum(CharRecordKind.KillNpc, 2, 0, 3);

        await Assert.That(achievements.IsCompleted(1000)).IsTrue();
        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(5u);
        await Assert.That(achievements.IsCompleted(1001)).IsTrue();
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(2u);
        await Assert.That(achievements.IsCompleted(1002)).IsFalse();
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(2u);
        await Assert.That(achievements.IsCompleted(1003)).IsFalse();

        achievements.UpdateMaximum(CharRecordKind.KillNpc, 3, 0, 1);

        await Assert.That(achievements.IsCompleted(1002)).IsTrue();
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(3u);
        await Assert.That(achievements.IsCompleted(1003)).IsTrue();
        await Assert.That(achievements.GetAmount(1003)).IsEqualTo(1u);
    }

    [Test]
    public async Task Increment_ObjectiveUnitRequirements_UseAndOrRaceAndMotherFactionRules()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.KillNpc, 1);
        for (uint index = 0; index < 4; index++)
        {
            data.AddAchievement(1000 + index, 1, false);
            data.AddObjective(1 + index, 1000 + index, 100, orUnitReqs: index >= 2);
        }

        data.AddUnitRequirement(1, 1, UnitReqsKindType.Race, (uint)Race.Nuian);
        data.AddUnitRequirement(2, 1, UnitReqsKindType.MotherFaction, (uint)FactionsEnum.NuiaAlliance);
        data.AddUnitRequirement(3, 2, UnitReqsKindType.Race, (uint)Race.Nuian);
        data.AddUnitRequirement(4, 2, UnitReqsKindType.MotherFaction, (uint)FactionsEnum.HaranyaAlliance);
        data.AddUnitRequirement(5, 3, UnitReqsKindType.Race, (uint)Race.Nuian);
        data.AddUnitRequirement(6, 3, UnitReqsKindType.MotherFaction, (uint)FactionsEnum.HaranyaAlliance);
        data.AddUnitRequirement(7, 4, UnitReqsKindType.Race, (uint)Race.Ferre);
        data.AddUnitRequirement(8, 4, UnitReqsKindType.MotherFaction, (uint)FactionsEnum.HaranyaAlliance);

        var character = new CharacterMock
        {
            Race = Race.Nuian,
            Faction = new SystemFaction
            {
                Id = FactionsEnum.Nuian,
                MotherId = FactionsEnum.NuiaAlliance
            }
        };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            TimeProvider.System,
            null,
            unitRequirementsData: data.BuildUnitRequirements(),
            templateMotherFactionResolver: _ => FactionsEnum.NuiaAlliance);

        character.Faction = new SystemFaction
        {
            Id = FactionsEnum.Pirate,
            MotherId = FactionsEnum.HaranyaAlliance
        };

        achievements.Increment(CharRecordKind.KillNpc, 1, 0);

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(0u);
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(1u);
        await Assert.That(achievements.GetAmount(1003)).IsEqualTo(0u);
    }

    [Test]
    public async Task Reconcile_ObjectiveRequirement_PreservesProgressAndCompletedState()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.CompleteQuestCategory, 3);
        data.AddAchievement(799, 1, false);
        data.AddObjective(1469, 799, 100);
        data.AddUnitRequirement(1, 1469, UnitReqsKindType.Race, (uint)Race.Nuian);
        var character = new CharacterMock { Race = Race.Ferre };
        var achievements = new CharacterAchievements(
            character,
            data.Build(),
            TimeProvider.System,
            null,
            unitRequirementsData: data.BuildUnitRequirements());

        achievements.Increment(CharRecordKind.CompleteQuestCategory, 3, 0);

        await Assert.That(achievements.GetAmount(799)).IsEqualTo(0u);
        await Assert.That(achievements.IsCompleted(799)).IsFalse();

        character.Race = Race.Nuian;
        achievements.ReconcileAuthoritativeState();

        await Assert.That(achievements.GetAmount(799)).IsEqualTo(1u);
        await Assert.That(achievements.IsCompleted(799)).IsTrue();

        character.Race = Race.Ferre;
        achievements.ReconcileAuthoritativeState();

        await Assert.That(achievements.GetAmount(799)).IsEqualTo(1u);
        await Assert.That(achievements.IsCompleted(799)).IsTrue();
    }

    [Test]
    public async Task ConfirmedSources_KeepSelectorsAndHourBoundariesIndependent()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(26, CharRecordKind.AbilityLevel, 1);
        data.AddRecord(27, CharRecordKind.AbilityLevel, 2);
        data.AddRecord(56, CharRecordKind.PlayTime);
        data.AddAchievement(9, 50, false);
        data.AddObjective(1, 9, 26);
        data.AddAchievement(10, 50, false);
        data.AddObjective(2, 10, 27);
        data.AddAchievement(31, 1, false);
        data.AddObjective(3, 31, 56);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 29, 16, 0, 0, TimeSpan.Zero));
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build(), timeProvider);

        achievements.UpdateAbilityLevel(AbilityType.Fight, 49);
        achievements.UpdatePlayTime(TimeSpan.FromMinutes(59));

        await Assert.That(achievements.GetAmount(9)).IsEqualTo(49u);
        await Assert.That(achievements.GetAmount(10)).IsEqualTo(0u);
        await Assert.That(achievements.GetAmount(31)).IsEqualTo(0u);

        achievements.UpdateAbilityLevel(AbilityType.Fight, 50);
        achievements.UpdatePlayTime(TimeSpan.FromHours(1));
        var abilityCompletion = achievements.GetCompletionTime(9);
        var playTimeCompletion = achievements.GetCompletionTime(31);

        await Assert.That(achievements.IsCompleted(9)).IsTrue();
        await Assert.That(achievements.IsCompleted(10)).IsFalse();
        await Assert.That(achievements.IsCompleted(31)).IsTrue();

        timeProvider.Advance(TimeSpan.FromDays(1));
        achievements.UpdateAbilityLevel(AbilityType.Fight, 55);
        achievements.UpdatePlayTime(TimeSpan.FromHours(2));

        await Assert.That(achievements.GetCompletionTime(9)).IsEqualTo(abilityCompletion);
        await Assert.That(achievements.GetCompletionTime(31)).IsEqualTo(playTimeCompletion);
    }

    [Test]
    public async Task CreateSnapshotPackets_FiftyOneEntries_UsesFiftyAndOneChunks()
    {
        using var data = new AchievementDataBuilder();
        for (uint index = 1; index <= 51; index++)
        {
            var achievementId = 2000 + index;
            data.AddRecord(index, CharRecordKind.KillNpc, index);
            data.AddAchievement(achievementId, 2, false);
            data.AddObjective(index, achievementId, index);
        }
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());
        for (uint index = 1; index <= 51; index++)
            achievements.UpdateMaximum(CharRecordKind.KillNpc, index, 0, 1);

        var packets = achievements.CreateSnapshotPackets();

        await Assert.That(packets.Count).IsEqualTo(2);
        var first = packets[0].Write(new PacketStream());
        first.Rollback();
        await Assert.That(first.ReadInt32()).IsEqualTo(50);
        for (uint index = 1; index <= 50; index++)
        {
            await Assert.That(first.ReadUInt32()).IsEqualTo(2000u + index);
            await Assert.That(first.ReadUInt32()).IsEqualTo(1u);
            await Assert.That(first.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));
        }
        await Assert.That(first.LeftBytes).IsEqualTo(0);

        var second = packets[1].Write(new PacketStream());
        second.Rollback();
        await Assert.That(second.ReadInt32()).IsEqualTo(1);
        await Assert.That(second.ReadUInt32()).IsEqualTo(2051u);
        await Assert.That(second.ReadUInt32()).IsEqualTo(1u);
        await Assert.That(second.ReadInt64()).IsEqualTo(Helpers.UnixTime(DateTime.MinValue));
        await Assert.That(second.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Reconcile_DurableCounters_RestoresSafeAchievementMinimums()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.GetLifePoint);
        data.AddRecord(101, CharRecordKind.GetJuryPoint);
        data.AddRecord(102, CharRecordKind.Judgement, 0);
        data.AddRecord(103, CharRecordKind.Judgement, 1);
        data.AddRecord(104, CharRecordKind.MyGold);
        data.AddRecord(105, CharRecordKind.SpendLabor);
        data.AddRecord(106, CharRecordKind.GetActability, 7);
        data.AddRecord(107, CharRecordKind.GetFaction, (uint)FactionsEnum.Pirate);
        data.AddAchievement(1000, 1000, false);
        data.AddAchievement(1001, 1000, false);
        data.AddAchievement(1002, 1000, false);
        data.AddAchievement(1003, 1000, false);
        data.AddAchievement(1004, 1000, false);
        data.AddAchievement(1005, 1000, false);
        data.AddAchievement(1006, 1000, false);
        data.AddAchievement(1007, 1000, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1001, 101);
        data.AddObjective(3, 1002, 102);
        data.AddObjective(4, 1003, 103);
        data.AddObjective(5, 1004, 104);
        data.AddObjective(6, 1005, 105);
        data.AddObjective(7, 1006, 106);
        data.AddObjective(8, 1007, 107);
        var character = new CharacterMock
        {
            VocationPoint = 450,
            JuryPoint = 8,
            NotGuiltyCount = 3,
            GuiltyCount = 4,
            Money = 1_230_000,
            ConsumedLaborPower = 789,
            Faction = new SystemFaction { Id = FactionsEnum.Pirate }
        };
        character.Actability = new CharacterActability(character);
        character.Actability.Actabilities[7] = new Actability(new ActabilityTemplate { Id = 7 })
        {
            Point = 456
        };
        var achievements = new CharacterAchievements(character, data.Build());

        achievements.ReconcileAuthoritativeState();

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(450u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(7u);
        await Assert.That(achievements.GetAmount(1002)).IsEqualTo(3u);
        await Assert.That(achievements.GetAmount(1003)).IsEqualTo(4u);
        await Assert.That(achievements.GetAmount(1004)).IsEqualTo(123u);
        await Assert.That(achievements.GetAmount(1005)).IsEqualTo(789u);
        await Assert.That(achievements.GetAmount(1006)).IsEqualTo(456u);
        await Assert.That(achievements.GetAmount(1007)).IsEqualTo(1u);
    }

    [Test]
    public async Task QuestCompletion_RepeatableTypeDoesNotRepeatCategoryProgress()
    {
        using var data = new AchievementDataBuilder();
        data.AddRecord(100, CharRecordKind.CompleteQuestCategory, 35);
        data.AddRecord(101, CharRecordKind.CompleteQuestType, 2941);
        data.AddAchievement(1000, 10, false);
        data.AddAchievement(1001, 10, false);
        data.AddObjective(1, 1000, 100);
        data.AddObjective(2, 1001, 101);
        var achievements = new CharacterAchievements(new CharacterMock(), data.Build());

        achievements.Increment([
            new AchievementProgressEvent(CharRecordKind.CompleteQuestType, 2941, 0, 1),
            new AchievementProgressEvent(CharRecordKind.CompleteQuestCategory, 35, 0, 1)
        ]);
        achievements.Increment(CharRecordKind.CompleteQuestType, 2941, 0);

        await Assert.That(achievements.GetAmount(1000)).IsEqualTo(1u);
        await Assert.That(achievements.GetAmount(1001)).IsEqualTo(2u);
    }

    private static void AddLevelAchievements(AchievementDataBuilder data)
    {
        data.AddRecord(9, CharRecordKind.CharLevel);

        data.AddAchievement(1, 0, true);
        uint objectiveId = 1;
        uint completionRecordId = 10;
        for (uint achievementId = 2; achievementId <= 7; achievementId++)
        {
            data.AddAchievement(achievementId, achievementId switch
            {
                2 => 5,
                3 => 10,
                4 => 20,
                5 => 30,
                6 => 40,
                _ => 50
            }, false);
            data.AddObjective(objectiveId++, achievementId, 9);
            data.AddRecord(completionRecordId, CharRecordKind.CompleteAchievement, achievementId);
            data.AddObjective(objectiveId++, 1, completionRecordId++);
            if (achievementId > 2)
                data.AddPrerequisite(achievementId - 2, achievementId, achievementId - 1);
        }

        data.AddAchievement(1478, 0, true);
        for (uint achievementId = 1479; achievementId <= 1483; achievementId++)
        {
            data.AddAchievement(achievementId, achievementId - 1428, false);
            data.AddObjective(objectiveId++, achievementId, 9);
            data.AddRecord(completionRecordId, CharRecordKind.CompleteAchievement, achievementId);
            data.AddObjective(objectiveId++, 1478, completionRecordId++);
        }
    }

    private static void AssertPacketOpcode(byte[] packet, ushort opcode)
    {
        if (packet.Length < 8 || BitConverter.ToUInt16(packet, 6) != opcode)
            throw new InvalidOperationException($"Expected opcode 0x{opcode:X3}.");
    }

    internal sealed class AchievementDataBuilder : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public AchievementDataBuilder()
        {
            _connection.Open();
            Execute("""
                CREATE TABLE achievements (
                    id INTEGER PRIMARY KEY,
                    category_id INTEGER,
                    complete_num INTEGER,
                    complete_or NUM,
                    icon_id INTEGER,
                    is_active NUM,
                    is_hidden NUM,
                    item_id INTEGER,
                    or_unit_reqs NUM,
                    parent_achievement_id INTEGER,
                    priority INTEGER,
                    sub_category_id INTEGER
                );
                CREATE TABLE achievement_objectives (
                    id INTEGER PRIMARY KEY,
                    achievement_id INTEGER,
                    or_unit_reqs NUM,
                    record_id INTEGER
                );
                CREATE TABLE pre_completed_achievements (
                    id INTEGER PRIMARY KEY,
                    my_achievement_id INTEGER,
                    completed_achievement_id INTEGER
                );
                CREATE TABLE char_records (
                    id INTEGER PRIMARY KEY,
                    kind_id INTEGER,
                    value1 INTEGER,
                    value2 INTEGER
                );
                CREATE TABLE unit_reqs (
                    id INTEGER PRIMARY KEY,
                    owner_id INTEGER,
                    owner_type TEXT,
                    kind_id INTEGER,
                    value1 INTEGER,
                    value2 INTEGER
                );
                """);
        }

        public void AddAchievement(uint id, uint completeNum, bool completeOr, uint itemId = 0)
        {
            Execute($"""
                INSERT INTO achievements
                    (id, category_id, complete_num, complete_or, icon_id, is_active, is_hidden, item_id,
                     or_unit_reqs, parent_achievement_id, priority, sub_category_id)
                VALUES ({id}, 1, {completeNum}, '{ToSqlBoolean(completeOr)}', 0, 't', 'f', {itemId}, 'f', 0, {id}, 1);
                """);
        }

        public void AddObjective(uint id, uint achievementId, uint recordId, bool orUnitReqs = false)
        {
            Execute($"""
                INSERT INTO achievement_objectives (id, achievement_id, or_unit_reqs, record_id)
                VALUES ({id}, {achievementId}, '{ToSqlBoolean(orUnitReqs)}', {recordId});
                """);
        }

        public void AddUnitRequirement(
            uint id,
            uint objectiveId,
            UnitReqsKindType kind,
            uint value1,
            uint value2 = 0)
        {
            Execute($"""
                INSERT INTO unit_reqs (id, owner_id, owner_type, kind_id, value1, value2)
                VALUES ({id}, {objectiveId}, 'AchievementObjective', {(uint)kind}, {value1}, {value2});
                """);
        }

        public void AddRecord(uint id, CharRecordKind kind, uint value1 = 0, uint value2 = 0)
        {
            Execute($"INSERT INTO char_records (id, kind_id, value1, value2) VALUES ({id}, {(uint)kind}, {value1}, {value2});");
        }

        public void AddPrerequisite(uint id, uint achievementId, uint completedAchievementId)
        {
            Execute($"""
                INSERT INTO pre_completed_achievements (id, my_achievement_id, completed_achievement_id)
                VALUES ({id}, {achievementId}, {completedAchievementId});
                """);
        }

        public AchievementGameData Build()
        {
            var gameData = new AchievementGameData();
            gameData.Load(_connection);
            gameData.PostLoad();
            return gameData;
        }

        public UnitRequirementsGameData BuildUnitRequirements()
        {
            var gameData = new UnitRequirementsGameData();
            gameData.Load(_connection);
            gameData.PostLoad();
            return gameData;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static char ToSqlBoolean(bool value)
        {
            return value ? 't' : 'f';
        }
    }

    private sealed class RecordingSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];

        public List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet)
        {
            Packets.Add(packet.ToArray());
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
