using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Team;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Items.Containers;

[NotInParallel]
public sealed class LootingContainerTests
{
    private QuestInteractionTestModels _models;

    [Before(Test)]
    public void SetUp() => _models = new QuestInteractionTestModels();

    [After(Test)]
    public void TearDown() => _models.Dispose();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenerateLoot_TaggedTeamOutsideEligibility_DoesNotGiveKillerPrivateRights(bool offline)
    {
        var setup = new LootSetup();
        var tagger = setup.AddPlayer(1, eligible: false);
        var killer = setup.AddPlayer(9, eligible: false);
        if (offline)
            tagger.Connection.Shutdown();
        else
            tagger.Transform.Local.SetPosition(201, 0, 0);
        var team = new Team { Id = 1, OwnerId = tagger.Id, Members = [new TeamMember(tagger)], LootingRule = setup.Rule };
        var teams = new TeamManager(null, null, null);
        ((ConcurrentDictionary<uint, Team>)typeof(TeamManager).GetField("_activeTeams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(teams)!).TryAdd(team.Id, team);
        var items = new ItemManager(null, null, null, null, null, null);
        typeof(ItemManager).GetField("_lootPackDroppingNpc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(items, new Dictionary<uint, List<LootPackDroppingNpc>> { [0] = [new() { LootPackId = 1 }] });
        var loot = new LootGameData();
        typeof(LootGameData).GetField("_lootPacks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(loot, new Dictionary<uint, LootPack>());
        using var teamsScope = new SingletonScope<TeamManager>(teams);
        using var itemsScope = new SingletonScope<ItemManager>(items);
        using var lootScope = new SingletonScope<LootGameData>(loot);
        var tagging = ((Npc)setup.Owner).CharacterTagging;
        typeof(Tagging).GetField("_tagger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tagging, tagger);
        typeof(Tagging).GetField("_tagTeam", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tagging, team.Id);

        setup.Container.GenerateLoot(killer);
        await Assert.That((HashSet<Character>)setup.GetProperty("EligiblePlayers")).IsEmpty();
        // Keep this test independent of drop-table randomness, then exercise the generated authorization state.
        setup.Container.Items.Add(1, setup.Entry);
        await Assert.That(setup.Container.TryTakeLoot(killer, 1, null, false)).IsFalse();
        setup.Container.MakeLootPublic();
        await Assert.That(setup.Container.TryTakeLoot(killer, 1, null, false)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pickup_OutsiderBeforePublic_CannotOpenOrTake(bool lootAll)
    {
        var setup = new LootSetup();
        var outsider = setup.AddPlayer(9, eligible: false);
        setup.Container.OpenBag(outsider, null, lootAll);
        var result = setup.Container.TryTakeLoot(outsider, 1, null, false);
        await Assert.That(result).IsFalse();
        await Assert.That(outsider.Money).IsEqualTo(0L);
        await Assert.That(setup.Entry.Item.Count).IsEqualTo(7);
        await Assert.That(setup.Container.Items.Count).IsEqualTo(1);
        await Assert.That(setup.Packets(outsider)).IsEmpty();
    }

    [Test]
    [Arguments(2.99f, 0f, true)]
    [Arguments(3f, 0f, false)]
    [Arguments(3.01f, 0f, false)]
    [Arguments(0f, 2.99f, true)]
    [Arguments(0f, 3f, false)]
    [Arguments(0f, 3.01f, false)]
    [Arguments(float.NaN, 0f, false)]
    [Arguments(float.PositiveInfinity, 0f, false)]
    public async Task Pickup_RangeBoundary_UsesFiniteThreeDimensionalDistance(float x, float z, bool expected)
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        player.Transform.Local.SetPosition(x, 0, z);
        setup.Container.OpenBag(player, null, false);
        await Assert.That(setup.Packets(player).Any(packet => Opcode(packet) == SCOffsets.SCLootBagDataPacket)).IsEqualTo(expected);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsEqualTo(expected);
        await Assert.That(player.Money).IsEqualTo(expected ? 7L : 0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pickup_AnotherInstanceOrMissingWorld_CannotOpenOrTake(bool missingWorld)
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        SetWorld(player, missingWorld ? null : new WorldInstance(setup.World.Template, 0, true, 2));
        setup.Container.OpenBag(player, null, true);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsFalse();
        await Assert.That(player.Money).IsEqualTo(0L);
        await Assert.That(setup.Packets(player)).IsEmpty();
    }

    [Test]
    public async Task Pickup_Public_AllowsNearbyOutsiderButRejectsDistantOutsider()
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(9, eligible: false);
        setup.Container.MakeLootPublic();
        player.Transform.Local.SetPosition(201, 0, 0);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsFalse();
        player.Transform.Local.SetPosition(0, 0, 0);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsTrue();
        await Assert.That(player.Money).IsEqualTo(7L);
    }

    [Test]
    [Arguments(8.99f, true)]
    [Arguments(9f, false)]
    [Arguments(9.01f, false)]
    public async Task Pickup_Doodad_UsesNativeNineMeterLimit(float distance, bool expected)
    {
        var setup = new LootSetup(LootOwnerType.Doodad);
        var player = setup.AddPlayer(1);
        player.Transform.Local.SetPosition(0, 0, distance);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsEqualTo(expected);
    }

    [Test]
    public async Task Pickup_ActorRadiiAndScale_AdjustSurfaceDistance()
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        player.ModelId = 1; // Fixture actor radius: 0.5 m.
        ((Npc)setup.Owner).ModelId = 1;
        ((Npc)setup.Owner).Template.Scale = 4;
        player.Transform.Local.SetPosition(5.49f, 0, 0);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsTrue();
    }

    [Test]
    public async Task Roll_PartyWinnerOutsidePickupRange_ReceivesAutomaticDistributionWithinTwoHundredMeters()
    {
        var setup = new LootSetup(1, 100);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        second.Transform.Local.SetPosition(200, 0, 0);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(second.Money).IsEqualTo(7L);
    }

    [Test]
    public async Task Pickup_StaleItemReference_CannotGrantTwice()
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        await Assert.That(setup.Container.TryTakeLoot(player, 1, setup.Entry, false)).IsTrue();
        await Assert.That(setup.Container.TryTakeLoot(player, 1, setup.Entry, false)).IsFalse();
        await Assert.That(player.Money).IsEqualTo(7L);
    }

    [Test]
    public async Task Pickup_QuestItem_DoesNotGrantToCharacterWithoutQuest()
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        setup.Entry.Item.Template.LootQuestId = 123;
        await Assert.That(setup.Container.TryTakeLoot(player, 1, null, false)).IsFalse();
        await Assert.That(player.Money).IsEqualTo(0L);
        await Assert.That(setup.Container.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Roll_NoActiveRollOrInvalidItem_DoesNotAddPlayerOrGrant()
    {
        var setup = new LootSetup(80);
        var player = setup.AddPlayer(1);
        setup.Container.DoPlayerRoll(player, 1, true);
        setup.Container.DoPlayerRoll(player, 999, true);
        await Assert.That(setup.Entry.PlayerRolls).IsEmpty();
        await Assert.That(player.Money).IsEqualTo(0L);
        await Assert.That(setup.Random.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Roll_OutsiderAndDuplicateReplies_DoNotChangeThePoolOrResult()
    {
        var setup = new LootSetup(80, 20);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var outsider = setup.AddPlayer(9, eligible: false);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(outsider, 1, true);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(first, 1, false);
        await Assert.That(setup.Entry.PlayerRolls.Count).IsEqualTo(2);
        await Assert.That(setup.Entry.PlayerRolls[first]).IsEqualTo((sbyte)80);
        await Assert.That(setup.Random.Calls).IsEqualTo(1);
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Container.DoPlayerRoll(first, 1, true);
        await Assert.That(first.Money).IsEqualTo(7L);
        await Assert.That(outsider.Money).IsEqualTo(0L);
        await Assert.That(setup.Random.Calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(LootingRuleMethod.FreeForAll)]
    [Arguments(LootingRuleMethod.RotateWinner)]
    [Arguments(LootingRuleMethod.LootMaster)]
    public async Task Roll_ThreePlayers_HighestInclusiveHundredWinsBeforeDistributionRule(LootingRuleMethod method)
    {
        var setup = new LootSetup(1, 100, 60);
        setup.Rule.LootMethod = method;
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var third = setup.AddPlayer(3);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Container.DoPlayerRoll(third, 1, true);
        await Assert.That(new[] { first.Money, second.Money, third.Money }).IsEquivalentTo([0L, 7L, 0L]);
        await Assert.That(setup.Entry.HighestRoller).IsEqualTo(second.Id);
        await Assert.That(setup.Entry.RollInProgress).IsFalse();
        await Assert.That(setup.Entry.RollCompleted).IsTrue();
        await Assert.That(setup.Container.Items).IsEmpty();
        var summary = setup.LastSummary(first);
        await Assert.That(summary).IsEquivalentTo(new Dictionary<uint, sbyte> { [1] = 1, [2] = 100, [3] = 60 });
    }

    [Test]
    public async Task Roll_RepeatedTies_RerollsOnlyTheHighestPlayersUntilOneWins()
    {
        // Round 1: players 1/2 tie at 90. Round 2 ties again. Round 3 awards player 2.
        var setup = new LootSetup(90, 90, 10, 30, 30, 1, 2);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var third = setup.AddPlayer(3);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Container.DoPlayerRoll(third, 1, true);
        await Assert.That(setup.Random.Calls).IsEqualTo(7);
        await Assert.That(setup.Entry.HighestRoller).IsEqualTo(second.Id);
        await Assert.That(second.Money).IsEqualTo(7L);
        await Assert.That(third.Money).IsEqualTo(0L);
        await Assert.That(setup.LastSummary(third)).IsEquivalentTo(new Dictionary<uint, sbyte> { [1] = 1, [2] = 2 });
        await Assert.That(setup.Packets(first).Count(packet => Opcode(packet) == SCOffsets.SCLootDicePacket)).IsEqualTo(1);
    }

    [Test]
    public async Task Roll_AllPass_LaterEligiblePickupDoesNotRestartDice()
    {
        var setup = new LootSetup();
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var outsider = setup.AddPlayer(3, eligible: false);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, false);
        setup.Container.DoPlayerRoll(second, 1, false);
        await Assert.That(setup.Entry.HighestRoller).IsEqualTo(0U);
        await Assert.That(setup.Container.TryTakeLoot(outsider, 1, null, false)).IsFalse();
        await Assert.That(setup.Container.TryTakeLoot(second, 1, null, false)).IsTrue();
        await Assert.That(second.Money).IsEqualTo(7L);
        await Assert.That(setup.Scheduled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Roll_Timeout_PassesOnlyUnansweredPlayersAndGrantsWinnerOnce()
    {
        var setup = new LootSetup(40);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var third = setup.AddPlayer(3);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Scheduled.Single().Task.Execute();
        setup.Scheduled.Single().Task.Execute();
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(setup.Scheduled.Single().Delay).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(first.Money).IsEqualTo(7L);
        await Assert.That(setup.Entry.PlayerRolls[second]).IsEqualTo((sbyte)-1);
        await Assert.That(setup.Entry.PlayerRolls[third]).IsEqualTo((sbyte)-1);
        await Assert.That(setup.Random.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Roll_PlayerLeavesRangeOrInstance_RejectsResponseAndPassesAtTimeout(bool otherInstance)
    {
        var setup = new LootSetup(60);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        setup.StartRoll(first);
        if (otherInstance)
            SetWorld(second, new WorldInstance(setup.World.Template, 0, true, 2));
        else
            second.Transform.Local.SetPosition(201, 0, 0);
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(setup.Entry.PlayerRolls[second]).IsEqualTo((sbyte)0);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Scheduled.Single().Task.Execute();
        await Assert.That(first.Money).IsEqualTo(7L);
        await Assert.That(second.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task Roll_WinnerMovesBeforeLastReply_KeepsClaimUntilNearbyPickup()
    {
        var setup = new LootSetup(100, 1);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var outsider = setup.AddPlayer(3, eligible: false);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        first.Transform.Local.SetPosition(201, 0, 0);
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Container.MakeLootPublic();
        await Assert.That(first.Money).IsEqualTo(0L);
        await Assert.That(setup.Container.TryTakeLoot(outsider, 1, null, false)).IsFalse();
        first.Transform.Local.SetPosition(0, 0, 0);
        await Assert.That(setup.Container.TryTakeLoot(first, 1, null, false)).IsTrue();
        await Assert.That(first.Money).IsEqualTo(7L);
    }

    [Test]
    public async Task Roll_WinnerDisconnects_PreservesClaimForReconnectedCharacter()
    {
        var setup = new LootSetup(100, 1);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        first.Connection.Shutdown();
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(first.Money).IsEqualTo(0L);
        await Assert.That(setup.Entry.HighestRoller).IsEqualTo(first.Id);
        var reconnected = setup.AddPlayer(first.Id, eligible: false);
        await Assert.That(setup.Container.TryTakeLoot(reconnected, 1, null, false)).IsTrue();
        await Assert.That(reconnected.Money).IsEqualTo(7L);
    }

    [Test]
    public async Task Roll_ReconnectedCharacter_CannotAnswerThePreviousSessionPool()
    {
        var setup = new LootSetup(100);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        setup.StartRoll(first);
        first.Connection.Shutdown();
        var reconnected = setup.AddPlayer(first.Id, eligible: false);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(reconnected, 1, true);
        await Assert.That(setup.Entry.PlayerRolls[first]).IsEqualTo((sbyte)0);
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Scheduled.Single().Task.Execute();
        await Assert.That(second.Money).IsEqualTo(7L);
        await Assert.That(reconnected.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task Roll_ReconnectBeforePoolCreation_UsesCurrentEligibleSession()
    {
        var setup = new LootSetup(1, 100);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        first.Connection.Shutdown();
        var reconnected = setup.AddPlayer(first.Id, eligible: false);
        setup.StartRoll(second);
        await Assert.That(setup.Entry.PlayerRolls.ContainsKey(first)).IsFalse();
        await Assert.That(setup.Entry.PlayerRolls.ContainsKey(reconnected)).IsTrue();
        setup.Container.DoPlayerRoll(second, 1, true);
        setup.Container.DoPlayerRoll(reconnected, 1, true);
        await Assert.That(reconnected.Money).IsEqualTo(7L);
        await Assert.That(first.Money).IsEqualTo(0L);
    }

    [Test]
    [Arguments(LootingRuleMethod.LootMaster)]
    [Arguments(LootingRuleMethod.RotateWinner)]
    public async Task Pickup_ReconnectedDistributionTarget_KeepsOriginalEligibility(LootingRuleMethod method)
    {
        var setup = new LootSetup();
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        first.Connection.Shutdown();
        var reconnected = setup.AddPlayer(first.Id, eligible: false);
        setup.Rule.LootMethod = method;
        setup.Rule.LootMaster = first.Id;
        setup.SetProperty("KillerTeam", new Team
        {
            Members = [new TeamMember(reconnected), new TeamMember(second) { HasGoneRoundRobin = true }]
        });
        await Assert.That(setup.Container.TryTakeLoot(second, 1, null, false)).IsFalse();
        await Assert.That(reconnected.Money).IsEqualTo(7L);
        await Assert.That(first.Money).IsEqualTo(0L);
    }

    [Test]
    public async Task Roll_WinnerBagFull_KeepsClaimForRetry()
    {
        var setup = new LootSetup(100, 1);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        first.Money = int.MaxValue;
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(setup.Container.Items.Count).IsEqualTo(1);
        await Assert.That(setup.Entry.HighestRoller).IsEqualTo(first.Id);
        first.Money = 0;
        await Assert.That(setup.Container.TryTakeLoot(first, 1, null, false)).IsTrue();
        await Assert.That(first.Money).IsEqualTo(7L);
    }

    [Test]
    public async Task MakePublic_ActiveRoll_FinishesBeforeOpeningUnclaimedLoot()
    {
        var setup = new LootSetup(50);
        var first = setup.AddPlayer(1);
        setup.AddPlayer(2);
        setup.StartRoll(first);
        setup.Container.DoPlayerRoll(first, 1, true);
        setup.Container.MakeLootPublic();
        await Assert.That(first.Money).IsEqualTo(7L);
        await Assert.That(setup.Entry.RollInProgress).IsFalse();
    }

    [Test]
    public async Task Roll_ConcurrentDuplicateResponses_UsesOneRollAndOneGrantPerPlayer()
    {
        var setup = new LootSetup(100, 1);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        setup.StartRoll(first);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => setup.Container.DoPlayerRoll(first, 1, true))));
        setup.Container.DoPlayerRoll(second, 1, true);
        await Assert.That(setup.Random.Calls).IsEqualTo(2);
        await Assert.That(first.Money).IsEqualTo(7L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LootItemPacket_ValidBody_UsesContainerEligibilityGate(bool eligible)
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1, eligible);
        var body = new PacketStream().Write((ushort)1).Write((ushort)LootOwnerType.Npc).WriteBc(77).Write((byte)0).Write(7);
        new CSLootItemPacket { Connection = player.Connection }.Read(body);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(player.Money).IsEqualTo(eligible ? 7L : 0L);
    }

    [Test]
    [Arguments(LootOwnerType.None, 77U, 1)]
    [Arguments(LootOwnerType.Doodad, 77U, 1)]
    [Arguments(LootOwnerType.Npc, 999U, 1)]
    [Arguments(LootOwnerType.Npc, 77U, 999)]
    public async Task LootItemPacket_InvalidIdentity_DoesNotGrant(LootOwnerType ownerType, uint ownerId, int itemIndex)
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        new CSLootItemPacket { Connection = player.Connection }.Read(new PacketStream()
            .Write((ushort)itemIndex).Write((ushort)ownerType).WriteBc(ownerId).Write((byte)0).Write(7));
        await Assert.That(player.Money).IsEqualTo(0L);
        await Assert.That(setup.Container.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LootPackets_TruncatedBodies_RejectBeforeChangingLootState()
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        var pickupBody = new PacketStream().Write(setup.Entry.Item.Id).Write(7).GetBytes();
        for (var length = 0; length < pickupBody.Length; length++)
        {
            var packet = new CSLootItemPacket { Connection = player.Connection };
            await Assert.That(() => packet.Read(new PacketStream(pickupBody[..length]))).Throws<MarshalException>();
        }
        var diceBody = new PacketStream().Write(setup.Entry.Item.Id).Write(true).GetBytes();
        for (var length = 0; length < diceBody.Length; length++)
        {
            var packet = new CSLootDicePacket { Connection = player.Connection };
            await Assert.That(() => packet.Read(new PacketStream(diceBody[..length]))).Throws<MarshalException>();
        }
        await Assert.That(player.Money).IsEqualTo(0L);
        await Assert.That(setup.Entry.PlayerRolls).IsEmpty();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(8)]
    [Arguments(int.MaxValue)]
    public async Task LootItemPacket_ClientCount_DoesNotOverrideServerQuantity(int count)
    {
        var setup = new LootSetup();
        var player = setup.AddPlayer(1);
        new CSLootItemPacket { Connection = player.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(count));
        await Assert.That(player.Money).IsEqualTo(7L);
        await Assert.That(setup.Container.Items).IsEmpty();
    }

    [Test]
    public async Task LootPackets_TrailingDataOrInvalidChoice_RejectWithoutMutation()
    {
        var setup = new LootSetup();
        var first = setup.AddPlayer(1);
        setup.AddPlayer(2);
        new CSLootItemPacket { Connection = first.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(7).Write((byte)0));
        setup.StartRoll(first);
        new CSLootDicePacket { Connection = first.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write((byte)2));
        new CSLootDicePacket { Connection = first.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(true).Write((byte)0));
        await Assert.That(first.Money).IsEqualTo(0L);
        await Assert.That(setup.Entry.PlayerRolls[first]).IsEqualTo((sbyte)0);
    }

    [Test]
    public async Task LootDicePacket_ValidNeedAndPassBodies_OnlyAuthorizesPendingPool()
    {
        var setup = new LootSetup(100);
        var first = setup.AddPlayer(1);
        var second = setup.AddPlayer(2);
        var outsider = setup.AddPlayer(9, eligible: false);
        var body = new PacketStream().Write(setup.Entry.Item.Id).Write(true);
        new CSLootDicePacket { Connection = first.Connection }.Read(body);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(setup.Entry.PlayerRolls).IsEmpty();
        setup.StartRoll(first);
        new CSLootDicePacket { Connection = outsider.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(true));
        new CSLootDicePacket { Connection = first.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(true));
        new CSLootDicePacket { Connection = second.Connection }.Read(new PacketStream().Write(setup.Entry.Item.Id).Write(false));
        await Assert.That(first.Money).IsEqualTo(7L);
        await Assert.That(second.Money).IsEqualTo(0L);
        await Assert.That(outsider.Money).IsEqualTo(0L);
        await Assert.That(setup.Random.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(50)]
    public async Task DiceSummary_NativeLimit_WritesEveryPairInStableOrder(int count)
    {
        var setup = new LootSetup();
        var rolls = Enumerable.Range(1, count).Reverse()
            .ToDictionary(id => (Character)setup.AddPlayer((uint)id), id => (sbyte)(id == 1 ? -1 : id));
        var body = new SCLootDiceSummaryPacket(LootOwnerType.Npc, 77, 1, rolls).Write(new PacketStream());
        await Assert.That(body.ReadUInt16()).IsEqualTo((ushort)1);
        await Assert.That(body.ReadUInt16()).IsEqualTo((ushort)LootOwnerType.Npc);
        await Assert.That(body.ReadBc()).IsEqualTo(77U);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(body.ReadInt32()).IsEqualTo(count);
        for (var id = 1; id <= count; id++)
        {
            await Assert.That(body.ReadUInt32()).IsEqualTo((uint)id);
            await Assert.That(body.ReadSByte()).IsEqualTo((sbyte)(id == 1 ? -1 : id));
        }
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task DiceSummary_AboveNativeLimit_RejectsBeforeWritingBody()
    {
        var setup = new LootSetup();
        var rolls = Enumerable.Range(1, 51).ToDictionary(id => (Character)setup.AddPlayer((uint)id), _ => (sbyte)1);
        var body = new PacketStream();
        var packet = new SCLootDiceSummaryPacket(LootOwnerType.Npc, 77, 1, rolls);
        await Assert.That(() => packet.Write(body)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(body.Count).IsEqualTo(0);
    }

    private static ushort Opcode(byte[] packet) => BitConverter.ToUInt16(packet, 6);

    private static void SetWorld(GameObject gameObject, WorldInstance world)
    {
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gameObject, world);
    }

    private sealed class LootSetup
    {
        private readonly Dictionary<Character, RecordingSession> _sessions = [];
        private readonly Dictionary<uint, Character> _currentPlayers = [];
        public WorldInstance World { get; } = new(new WorldTemplate { Id = 1 }, 0, true, 1);
        public LootingContainer Container { get; }
        public BaseUnit Owner { get; }
        public LootingContainerItemEntry Entry { get; }
        public LootingRule Rule { get; } = new() { LootMethod = LootingRuleMethod.FreeForAll, MinimumGrade = 0, RollForBindOnPickup = false };
        public QueueRandom Random { get; }
        public List<(AAEmu.Game.Models.Tasks.Task Task, TimeSpan Delay)> Scheduled { get; } = [];
        private HashSet<Character> Eligible => (HashSet<Character>)GetProperty("EligiblePlayers");

        public LootSetup(params int[] rolls) : this(LootOwnerType.Npc, rolls)
        {
        }

        public LootSetup(LootOwnerType ownerType, params int[] rolls)
        {
            Random = new QueueRandom(rolls);
            BaseUnit owner = ownerType == LootOwnerType.Npc
                ? new Npc { ObjId = 77, Template = new NpcTemplate() }
                : new Doodad { ObjId = 77 };
            Owner = owner;
            SetWorld(owner, World);
            Container = new LootingContainer(owner, Random, (task, delay) => Scheduled.Add((task, delay)),
                id => _currentPlayers.GetValueOrDefault(id));
            typeof(AAEmu.Game.Models.Game.Units.BaseUnit).GetProperty(nameof(owner.LootingContainer))!.SetValue(owner, Container);
            World.AddObject(owner);
            SetProperty("TeamLootingRule", Rule);
            SetProperty("LootOwnerType", ownerType);
            SetProperty("CreationTime", DateTime.UtcNow);
            var item = new Item(((ulong)owner.ObjId << 32) | ((ulong)ownerType << 16) | 1,
                new ItemTemplate { Id = Item.Coins, MaxCount = int.MaxValue }, 7) { Grade = 2 };
            Entry = new LootingContainerItemEntry { Owner = Container, ItemIndex = 1, Item = item };
            Container.Items.Add(1, Entry);
        }

        public CharacterMock AddPlayer(uint id, bool eligible = true)
        {
            var session = new RecordingSession();
            var character = new CharacterMock { Id = id, ObjId = id + 100, Name = $"Player{id}", Connection = new GameConnection(session) };
            SetWorld(character, World);
            character.Connection.ActiveChar = character;
            typeof(Character).GetField("<IsOnline>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(character, true);
            var inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
            typeof(Inventory).GetField("<Bag>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(inventory, new ItemContainer(id, SlotType.Inventory, false, character) { Owner = character });
            character.Inventory = inventory;
            typeof(Character).GetField("<Achievements>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(character, null);
            _sessions.Add(character, session);
            _currentPlayers[id] = character;
            if (eligible)
                Eligible.Add(character);
            return character;
        }

        public void StartRoll(Character player)
        {
            Rule.MinimumGrade = 2;
            Container.TryTakeLoot(player, 1, null, false);
        }

        public byte[][] Packets(Character player) => _sessions[player].Packets.ToArray();

        public Dictionary<uint, sbyte> LastSummary(Character player)
        {
            var body = new PacketStream(Packets(player).Last(packet => Opcode(packet) == SCOffsets.SCLootDiceSummaryPacket)[8..]);
            if (body.ReadUInt16() != 1 || body.ReadUInt16() != (ushort)LootOwnerType.Npc || body.ReadBc() != 77 || body.ReadByte() != 0)
                throw new InvalidOperationException("Unexpected loot identity in dice summary.");
            var count = body.ReadInt32();
            var result = new Dictionary<uint, sbyte>();
            for (var i = 0; i < count; i++)
                result.Add(body.ReadUInt32(), body.ReadSByte());
            if (body.LeftBytes != 0)
                throw new InvalidOperationException("Unexpected trailing dice summary bytes.");
            return result;
        }

        public object GetProperty(string name) => typeof(LootingContainer).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Container);
        public void SetProperty(string name, object value) => typeof(LootingContainer).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Container, value);
    }

    private sealed class SingletonScope<T> : IDisposable where T : class
    {
        private static readonly FieldInfo Field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previous = Field.GetValue(null);

        public SingletonScope(T value) => Field.SetValue(null, value);
        public void Dispose() => Field.SetValue(null, _previous);
    }

    private sealed class QueueRandom(params int[] rolls) : Random
    {
        private readonly Queue<int> _rolls = new(rolls);
        public int Calls { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            if (minValue != 1 || maxValue != 101)
                throw new InvalidOperationException("Loot dice must include 1 through 100.");
            Calls++;
            return _rolls.Dequeue();
        }
    }

    private sealed class RecordingSession : ISession
    {
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Enqueue(packet.ToArray());
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }
}
