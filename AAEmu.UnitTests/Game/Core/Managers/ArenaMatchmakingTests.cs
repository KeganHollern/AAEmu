using System.Collections.Concurrent;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Indun;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Xml;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public sealed partial class ArenaMatchmakingTests
{
    private readonly WorldManager _worlds = new(null, null, null, null, null);
    private readonly InstantGameManager _queue = new();
    private readonly ConcurrentDictionary<uint, WorldInstance> _instances = new();
    private readonly List<(FieldInfo Field, object Original)> _originals = [];

    [Before(Test)]
    public void SetUp()
    {
        var fields = new BattlefieldGameData();
        var camp = new Battlefield
        {
            Id = 7, ZoneKey = 198,
            Spawns = new() { Corps1Spawn = new() { ZoneId = 198 }, Corps2Spawn = new() { ZoneId = 198 } },
            RuleSet = new() { Id = 22, CorpsSize = 5, Corps1FactionId = 159, Corps2FactionId = 160 }
        };
        var duel = new Battlefield
        {
            Id = 11, ZoneKey = 302,
            Spawns = new() { Corps1Spawn = new() { ZoneId = 302 }, Corps2Spawn = new() { ZoneId = 302 } },
            RuleSet = new() { Id = 28, CorpsSize = 1, Corps1FactionId = 159, Corps2FactionId = 160 }
        };
        Set(fields, "_battlefields", new Dictionary<uint, Battlefield> { [camp.Id] = camp, [duel.Id] = duel });
        Install(fields);
        Install(_queue);

        // Queue tests borrow a fixed world. ArenaWorldRuntimeTests covers release of dedicated instances.
        var template = new WorldTemplate { Id = 0, Name = "arena", ZoneKeys = [camp.ZoneKey, duel.ZoneKey] };
        var zones = new ZoneManager(null, null);
        Set(zones, "_zones", template.ZoneKeys.ToDictionary(key => key, key => new Zone { ZoneKey = key }));
        Install(zones);
        template.XmlWorld.Zones = new ConcurrentDictionary<uint, XmlWorldZone>(
            template.ZoneKeys.ToDictionary(key => key, _ => new XmlWorldZone()));
        var world = new WorldInstance(template, 0, true, 42);
        GC.SuppressFinalize(world);
        _worlds.WorldTemplates.Add(template.Name, template);
        Set(_worlds, "<WorldNames>k__BackingField", new List<string> { template.Name });
        Set(_worlds, "_worldIdByZoneKey", template.ZoneKeys.ToDictionary(key => key, _ => template.Id));
        _instances[world.Id] = world;
        Set(_worlds, "_worlds", _instances);
        Install(_worlds);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, original) in _originals)
            field.SetValue(null, original);
    }

    [Test]
    public async Task StockBattlefields_QueueOnlyTheTwoModesWithBothTeamSpawns()
    {
        var fields = LoadStockFields();
        var applicant = AddApplicant(1);
        var session = Observe(applicant);
        foreach (var id in new uint[] { 1, 2, 3, 6, 10 })
        {
            await Assert.That(fields.GetBattlefield(id).RuleSet.BattlefieldId).IsEqualTo(id);
            _queue.ApplyToBattlefield(id, InstantCorps.Any, applicant);
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        }
        await Assert.That(Messages(session, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(0);
        await Assert.That(applicant.CurrentInstantGame).IsNull();

        foreach (var (id, size) in new[] { (7u, 10), (11u, 2) })
        {
            session.Packets.Clear();
            _queue.ApplyToBattlefield(id, InstantCorps.Any, applicant);
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            await Assert.That(Messages(session, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
            await Assert.That(applicant.CurrentInstantGame).IsNull();
            var others = Enumerable.Range((int)id * 10, size - 1).Select(AddApplicant).ToArray();
            foreach (var other in others)
                _queue.ApplyToBattlefield(id, InstantCorps.Any, other);
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            var match = applicant.CurrentInstantGame;
            await Assert.That(match.BattlefieldId).IsEqualTo(id);
            await Assert.That(others.All(other => other.CurrentInstantGame == match)).IsTrue();
            foreach (var member in others.Append(applicant))
                _queue.WithdrawFromBattlefield(member);
        }
    }

    [Test]
    [Arguments("rules")]
    [Arguments("size")]
    [Arguments("factions")]
    [Arguments("spawns")]
    [Arguments("firstTeam")]
    [Arguments("secondTeam")]
    [Arguments("zone")]
    [Arguments("position")]
    public async Task IncompleteMatchData_DoesNotEnterTheQueueOrDelayACompleteMode(string missing)
    {
        var field = BattlefieldGameData.Instance.GetBattlefield(7);
        switch (missing)
        {
            case "rules": field.RuleSet = null; break;
            case "size": field.RuleSet.CorpsSize = 0; break;
            case "factions": field.RuleSet.Corps2FactionId = field.RuleSet.Corps1FactionId; break;
            case "spawns": field.Spawns = null; break;
            case "firstTeam": field.Spawns.Corps1Spawn = null; break;
            case "secondTeam": field.Spawns.Corps2Spawn = null; break;
            case "zone": field.Spawns.Corps1Spawn.ZoneId = 302; break;
            case "position": field.Spawns.Corps2Spawn.Z = float.NaN; break;
        }

        var first = AddApplicant(1);
        var second = AddApplicant(2);
        var observer = Observe(first);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, first);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(0);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(first.CurrentInstantGame).IsNull();

        _queue.ApplyToBattlefield(11, InstantCorps.Any, first);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, second);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(first.CurrentInstantGame.BattlefieldId).IsEqualTo(11u);
        await Assert.That(second.CurrentInstantGame).IsSameReferenceAs(first.CurrentInstantGame);
    }

    [Test]
    public async Task FirstQueueSurvivesAnotherApplicationAndAllowsAChangeAfterCancel()
    {
        var player = AddApplicant(1);
        var opponent = AddApplicant(2);
        var observer = Observe(player);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, opponent);
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(opponent.CurrentInstantGame).IsNull();

        _queue.WithdrawFromBattlefield(player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.BattlefieldTick(tick);
        var duel = opponent.CurrentInstantGame;
        await Assert.That(duel.BattlefieldId).IsEqualTo(11u);
        await Assert.That(player.CurrentInstantGame).IsSameReferenceAs(duel);

        _queue.ApplyToBattlefield(7, InstantCorps.Any, player);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(2);
        duel.PlayerInviteResponse(player, false, 1); // Next room queues the original mode again.
        _queue.ApplyToBattlefield(7, InstantCorps.Any, player);
        _queue.BattlefieldTick(tick);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(3);
        await Assert.That(player.CurrentInstantGame).IsNull();
        _queue.WithdrawFromBattlefield(player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.BattlefieldTick(tick);
        await Assert.That(player.CurrentInstantGame).IsSameReferenceAs(duel);
    }

    [Test]
    [Arguments(InstantGameType.Gladiator, 2)]
    [Arguments(InstantGameType.DrillCamp, 10)]
    public async Task NextRoom_WaitsForAnotherMatchAndOffersTheVacancyToTheNextApplicant(InstantGameType mode, int playersNeeded)
    {
        var tick = TimeSpan.FromSeconds(15);
        var original = Enumerable.Range(1, playersNeeded).Select(AddApplicant).ToArray();
        var waiting = original[0];
        var observer = Observe(waiting);
        foreach (var member in original)
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, member);
        _queue.BattlefieldTick(tick);
        var declined = waiting.CurrentInstantGame;
        declined.PlayerInviteResponse(waiting, false, 1);
        _queue.BattlefieldTick(tick);
        await Assert.That(waiting.CurrentInstantGame).IsNull();
        await Assert.That(Messages(observer, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(1);

        var replacement = AddApplicant(playersNeeded + 1);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, replacement);
        _queue.BattlefieldTick(tick);
        await Assert.That(replacement.CurrentInstantGame).IsSameReferenceAs(declined);
        await Assert.That(waiting.CurrentInstantGame).IsNull();

        var next = Enumerable.Range(playersNeeded + 2, playersNeeded - 1).Select(AddApplicant).ToArray();
        foreach (var member in next)
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, member);
        _queue.BattlefieldTick(tick);
        await Assert.That(waiting.CurrentInstantGame).IsNotNull();
        await Assert.That(waiting.CurrentInstantGame == declined).IsFalse();
        await Assert.That(next.All(member => member.CurrentInstantGame == waiting.CurrentInstantGame)).IsTrue();
        await Assert.That(Messages(observer, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NextRoom_AfterAcceptanceKeepsThePlayerInTheSameMatch(bool enteredWorld)
    {
        var player = AddApplicant(1);
        var opponent = AddApplicant(2);
        var observer = Observe(player);
        foreach (var member in new[] { player, opponent })
        {
            member.Faction = new() { Id = member == player ? (FactionsEnum)159 : (FactionsEnum)160 };
            member.OriginFaction = member.Faction;
            _queue.ApplyToBattlefield(11, InstantCorps.Any, member);
        }
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        var match = player.CurrentInstantGame;
        Set(match, "_clock", new FakeTimeProvider());
        match.PlayerInviteResponse(player, true, 1);
        if (enteredWorld)
        {
            match.OnEnterWorld(player, 1);
            match.PlayerInviteResponse(opponent, true, 1);
            match.OnEnterWorld(opponent, 1);
        }

        match.PlayerInviteResponse(player, false, 1);
        match.PlayerInviteResponse(player, false, 1);
        await Assert.That(player.CurrentInstantGame).IsSameReferenceAs(match);
        _queue.BattlefieldTick(tick);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(Messages(observer, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task NextRoom_UsesAnotherOpenRoomWhenTheFirstRoomHasNoEligibleApplicant()
    {
        var tick = TimeSpan.FromSeconds(15);
        var players = Enumerable.Range(1, 4).Select(AddApplicant).ToArray();
        foreach (var player in players)
            _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.BattlefieldTick(tick);
        _queue.BattlefieldTick(tick);
        var firstRoom = players[0].CurrentInstantGame;
        var secondRoom = players[2].CurrentInstantGame;
        await Assert.That(firstRoom == secondRoom).IsFalse();

        firstRoom.PlayerInviteResponse(players[0], false, 1);
        _queue.WithdrawFromBattlefield(players[2]);
        _queue.BattlefieldTick(tick);
        await Assert.That(players[0].CurrentInstantGame).IsSameReferenceAs(secondRoom);
        await Assert.That(players[1].CurrentInstantGame).IsSameReferenceAs(firstRoom);
        await Assert.That(players[2].CurrentInstantGame).IsNull();
    }

    [Test]
    public async Task ConcurrentApplicationsReserveOnePlace()
    {
        var player = AddApplicant(1);
        var observer = Observe(player);
        await Task.WhenAll(
            Task.Run(() => _queue.ApplyToBattlefield(7, InstantCorps.Any, player)),
            Task.Run(() => _queue.ApplyToBattlefield(11, InstantCorps.Any, player)));
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacementSessionCanApplyAsSoonAsWaitingOrInvitedSessionLeaves(bool invited)
    {
        var first = AddApplicant(1);
        var oldObserver = Observe(first);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, first);
        var tick = TimeSpan.FromSeconds(15);
        if (invited)
        {
            _queue.ApplyToBattlefield(11, InstantCorps.Any, AddApplicant(2));
            _queue.BattlefieldTick(tick);
        }

        var returned = NewSession(1, 101);
        var observer = Observe(returned);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, returned);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(0);
        _worlds.TryRemoveCharacter(first.ObjId);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, returned);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        foreach (var teammate in Enumerable.Range(3, 9).Select(AddApplicant))
            _queue.ApplyToBattlefield(7, InstantCorps.Any, teammate);
        _queue.BattlefieldTick(tick);
        await Assert.That(returned.CurrentInstantGame.BattlefieldId).IsEqualTo(7u);
        await Assert.That(first.CurrentInstantGame).IsNull();
        await Assert.That(Messages(oldObserver, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(invited ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectedMemberCanReapplyBeforeOldMatchCleanup(bool inviteBeforeCleanup)
    {
        var first = AddApplicant(1);
        var opponent = AddApplicant(2);
        var observer = Observe(first);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, first);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, opponent);
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        var oldMatch = first.CurrentInstantGame;
        var invitation = Messages(observer, SCOffsets.SCInviteToInstantGamePacket).Single();
        first.Faction = new() { Id = (InstantCorps)invitation[20] == InstantCorps.Corps1 ? (FactionsEnum)159 : (FactionsEnum)160 };
        first.OriginFaction = first.Faction;
        oldMatch.PlayerInviteResponse(first, true, 1);
        oldMatch.OnEnterWorld(first, 1);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, first);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);

        _worlds.TryRemoveCharacter(first.ObjId);
        var returned = NewSession(1, first.ObjId);
        var newObserver = Observe(returned);
        _queue.ApplyToBattlefield(7, InstantCorps.Any, returned);
        foreach (var teammate in Enumerable.Range(3, 9).Select(AddApplicant))
            _queue.ApplyToBattlefield(7, InstantCorps.Any, teammate);
        if (inviteBeforeCleanup)
            _queue.BattlefieldTick(tick);

        // A late logout for the previous object cannot remove the replacement with the reused runtime ID.
        await Assert.That(_worlds.TryRemoveCharacter(first)).IsFalse();
        await Assert.That(_worlds.GetCharacterByObjId(returned.ObjId)).IsSameReferenceAs(returned);
        // The other invitation leaves normally before the old match ends.
        _queue.WithdrawFromBattlefield(opponent);
        await oldMatch.EndGame();
        _queue.BattlefieldTick(tick);
        await Assert.That(first.CurrentInstantGame).IsNull();
        await Assert.That(Messages(observer, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(Messages(newObserver, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(Messages(newObserver, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(returned.CurrentInstantGame.BattlefieldId).IsEqualTo(7u);
        await Assert.That(returned.CurrentInstantGame.IsFull).IsTrue();
    }

    [Test]
    public async Task APlayerWhoLeavesTheWorldBeforeTheTickDoesNotFillATeamSlot()
    {
        var first = new CharacterMock { Id = 1, ObjId = 10, Buffs = Mock.Of<IBuffs>().Object };
        var applicant = new CharacterMock { Id = 2, ObjId = 20, Buffs = Mock.Of<IBuffs>().Object };
        var returned = new CharacterMock { Id = first.Id, ObjId = 30, Buffs = Mock.Of<IBuffs>().Object };
        _worlds.TryAddCharacter(first);
        _worlds.TryAddCharacter(applicant);
        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, first);
        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, applicant);
        _worlds.TryRemoveCharacter(first.ObjId);
        _worlds.TryAddCharacter(returned);

        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(applicant.CurrentInstantGame).IsNull();
        await Assert.That(returned.CurrentInstantGame).IsNull();

        _queue.ApplyToBattlefield((uint)InstantGameType.Gladiator, InstantCorps.Any, returned);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(applicant.CurrentInstantGame).IsNotNull();
        await Assert.That(applicant.CurrentInstantGame.IsFull).IsTrue();
        await Assert.That(returned.CurrentInstantGame).IsSameReferenceAs(applicant.CurrentInstantGame);
    }

    [Test]
    [Arguments(InstantGameType.DrillCamp, 10)]
    [Arguments(InstantGameType.Gladiator, 2)]
    public async Task QueueWaitsForBothTeamsAndRefillsAVacancy(InstantGameType mode, int playersNeeded)
    {
        var players = Enumerable.Range(1, playersNeeded + 1).Select(id => new CharacterMock
        {
            Id = (uint)id, ObjId = (uint)id, Name = $"Player{id}", Buffs = Mock.Of<IBuffs>().Object
        }).ToArray();
        foreach (var player in players)
            _worlds.TryAddCharacter(player);

        foreach (var player in players.Take(playersNeeded - 1))
        {
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, player);
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            await Assert.That(player.CurrentInstantGame).IsNull();
        }

        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, players[playersNeeded - 1]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var match = players[0].CurrentInstantGame;
        await Assert.That(match).IsNotNull();
        await Assert.That(match.IsFull).IsTrue();
        await Assert.That(players.Take(playersNeeded).All(player => player.CurrentInstantGame == match)).IsTrue();

        _queue.WithdrawFromBattlefield(players[0]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(players[0].CurrentInstantGame).IsNull();
        await Assert.That(match.IsFull).IsFalse();
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, players[^1]);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(players[^1].CurrentInstantGame).IsSameReferenceAs(match);
        await Assert.That(match.IsFull).IsTrue();
    }

    [Test]
    [Arguments(InstantGameType.DrillCamp, 10, false)]
    [Arguments(InstantGameType.DrillCamp, 10, true)]
    [Arguments(InstantGameType.Gladiator, 2, false)]
    [Arguments(InstantGameType.Gladiator, 2, true)]
    public async Task EmptyInvitedMatch_DoesNotInviteTheNextSoloApplicant(InstantGameType mode, int teamTotal, bool disconnectLast)
    {
        var roster = Enumerable.Range(1, teamTotal).Select(AddApplicant).ToArray();
        foreach (var player in roster)
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, player);
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        var original = roster[0].CurrentInstantGame;
        var originalWorld = _worlds.GetWorlds().Single();
        await Assert.That(original.IsFull).IsTrue();

        foreach (var player in roster.SkipLast(1))
            _queue.WithdrawFromBattlefield(player);
        if (disconnectLast)
            _worlds.TryRemoveCharacter(roster[^1].ObjId);
        else
            _queue.WithdrawFromBattlefield(roster[^1]);

        var fresh = AddApplicant(teamTotal + 1);
        var session = Observe(fresh);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, fresh);
        _queue.BattlefieldTick(tick);
        await Assert.That(Messages(session, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        await Assert.That(Messages(session, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(0);
        await Assert.That(fresh.CurrentInstantGame).IsNull();
        await Assert.That(roster.All(player => player.CurrentInstantGame == null)).IsTrue();
        await Assert.That(_worlds.GetWorlds().Single()).IsSameReferenceAs(originalWorld);

        // Supply a new world with a reused ID. A late end of the old match must leave it intact.
        var next = new WorldInstance(originalWorld.Template, 0, true, originalWorld.Id);
        GC.SuppressFinalize(next);
        Set(_worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance>(new Dictionary<uint, WorldInstance> { [next.Id] = next }));
        for (var i = 0; i < teamTotal - 1; i++)
        {
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, roster[i]);
            _queue.BattlefieldTick(tick);
            if (i < teamTotal - 2)
                await Assert.That(Messages(session, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(0);
        }
        await Assert.That(fresh.CurrentInstantGame.IsFull).IsTrue();
        await Assert.That(fresh.CurrentInstantGame).IsNotSameReferenceAs(original);
        await Assert.That(Messages(session, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(1);
        await original.EndGame();
        await Assert.That(_worlds.GetWorlds().Single()).IsSameReferenceAs(next);
        var invitation = Messages(session, SCOffsets.SCInviteToInstantGamePacket).Single();
        await Assert.That(BitConverter.ToUInt32(invitation, 12)).IsEqualTo(next.Id);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SecondArenaVisit_ReturnsToTheLastMainWorldDeparture(bool enterFromOtherInstance)
    {
        var main = new WorldInstance(new WorldTemplate { Id = 1, Name = "main_world" }, 0, true, WorldManager.DefaultInstanceId);
        var island = new WorldInstance(new WorldTemplate { Id = 2, Name = "mirage_isle" }, 0, true, 93);
        GC.SuppressFinalize(main);
        GC.SuppressFinalize(island);
        _instances[main.Id] = main;
        _instances[island.Id] = island;
        var player = AddApplicant(1);
        var opponent = AddApplicant(2);
        var session = Observe(player);
        player.Faction = new() { Id = (FactionsEnum)159 };
        player.OriginFaction = player.Faction;
        player.Transform.Local.Position = new(10, 20, 30);
        player.Transform.InstanceId = WorldManager.DefaultInstanceId;
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, opponent);

        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var first = player.CurrentInstantGame;
        first.PlayerInviteResponse(player, true, 1);
        first.OnEnterWorld(player, 1);
        first.LeaveInstantGame(player);
        var original = player.Transform.World.Position;
        await Assert.That(original.X).IsEqualTo(10f);

        // A nested trip still returns to the last departure from the main world.
        if (enterFromOtherInstance)
            player.Transform.InstanceId = island.Id;
        player.Transform.Local.Position = new(80, 91, 102);
        var expected = enterFromOtherInstance ? original : player.Transform.World.Position;
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var second = player.CurrentInstantGame;
        second.PlayerInviteResponse(player, true, 1);
        second.OnEnterWorld(player, 1);
        second.LeaveInstantGame(player);

        await Assert.That(player.Transform.InstanceId).IsEqualTo(WorldManager.DefaultInstanceId);
        await Assert.That(player.Transform.World.Position).IsEqualTo(expected);
        var load = Messages(session, SCOffsets.SCLoadInstancePacket).Last();
        await Assert.That(BitConverter.ToSingle(load, 16)).IsEqualTo(expected.X);
        await Assert.That(BitConverter.ToSingle(load, 20)).IsEqualTo(expected.Y);
        await Assert.That(BitConverter.ToSingle(load, 24)).IsEqualTo(expected.Z);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DepartedMemberBeforeStart_GivesTheirPlaceToTheNextApplicant(bool disconnect)
    {
        var first = AddApplicant(1);
        var second = AddApplicant(2);
        var replacement = AddApplicant(3);
        var session = Observe(second);
        var rules = BattlefieldGameData.Instance.GetBattlefield(11).RuleSet;
        first.Faction = new() { Id = (FactionsEnum)rules.Corps1FactionId };
        second.Faction = new() { Id = (FactionsEnum)rules.Corps2FactionId };
        replacement.Faction = first.Faction;
        foreach (var player in new[] { first, second, replacement })
            player.OriginFaction = player.Faction;
        _queue.ApplyToBattlefield(11, InstantCorps.Any, first);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, second);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var match = first.CurrentInstantGame;
        Set(match, "_clock", new FakeTimeProvider());
        match.PlayerInviteResponse(first, true, 1);
        match.OnEnterWorld(first, 1);
        if (disconnect)
            _worlds.TryRemoveCharacter(first.ObjId);
        else
            match.LeaveInstantGame(first);
        match.PlayerInviteResponse(second, true, 1);
        match.OnEnterWorld(second, 1);
        await Assert.That(Messages(session, SCOffsets.SCInstantGameStartPacket).Count()).IsEqualTo(0);

        _queue.ApplyToBattlefield(11, InstantCorps.Any, replacement);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(replacement.CurrentInstantGame).IsSameReferenceAs(match);
        match.PlayerInviteResponse(replacement, true, 1);
        match.OnEnterWorld(replacement, 1);
        await Assert.That(Messages(session, SCOffsets.SCInstantGameStartPacket).Count()).IsEqualTo(1);
        await match.EndGame();
    }

    [Test]
    [Arguments("leave", false, false)]
    [Arguments("leave", true, false)]
    [Arguments("disconnect", true, false)]
    [Arguments("leave", false, true)]
    [Arguments("disconnect", true, true)]
    [Arguments("sentence", true, false)]
    public async Task LastDepartureBeforeStart_RetiresMatchAndRequiresANewOpponent(
        string departure, bool stillLoading, bool inviteeLeavesLast)
    {
        var player = AddApplicant(1);
        var opponent = AddApplicant(2);
        var session = Observe(player);
        var rules = BattlefieldGameData.Instance.GetBattlefield(11).RuleSet;
        player.Faction = new() { Id = (FactionsEnum)rules.Corps1FactionId };
        player.OriginFaction = player.Faction;
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, opponent);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var match = player.CurrentInstantGame;
        match.PlayerInviteResponse(player, true, 1);
        if (!stillLoading)
            match.OnEnterWorld(player, 1);
        if (!inviteeLeavesLast)
            _queue.WithdrawFromBattlefield(opponent);
        await Assert.That(match.IsFinished).IsFalse();

        switch (departure)
        {
            case "disconnect":
                _worlds.TryRemoveCharacter(player.ObjId);
                break;
            case "sentence":
                player.OfflineGuiltyTime = -1;
                match.OnEnterWorld(player, 1);
                break;
            default:
                match.LeaveInstantGame(player);
                break;
        }
        if (inviteeLeavesLast)
        {
            await Assert.That(match.IsFinished).IsFalse();
            _queue.WithdrawFromBattlefield(opponent);
        }

        await Assert.That(match.IsFinished).IsTrue();
        await Assert.That(Messages(session, SCOffsets.SCInstantGameEndPacket).Count()).IsEqualTo(0);
        await Assert.That(player.CurrentInstantGame).IsNull();
        if (departure == "leave")
            await Assert.That(player.Transform.InstanceId).IsEqualTo(WorldManager.DefaultInstanceId);

        var next = AddApplicant(3);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, next);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(next.CurrentInstantGame).IsNull();
        _queue.ApplyToBattlefield(11, InstantCorps.Any, opponent);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        await Assert.That(next.CurrentInstantGame).IsNotNull();
        await Assert.That(next.CurrentInstantGame).IsNotSameReferenceAs(match);
    }

    [Test]
    [Arguments(InstantGameType.Gladiator, 2)]
    [Arguments(InstantGameType.DrillCamp, 10)]
    public async Task AcceptedInvite_KeepsItsWorldUntilNormalEnd(InstantGameType mode, int teamTotal)
    {
        var roster = Enumerable.Range(1, teamTotal).Select(AddApplicant).ToArray();
        foreach (var player in roster)
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, player);
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        var match = roster[0].CurrentInstantGame;
        match.PlayerInviteResponse(roster[0], true, 1);
        await Assert.That(roster[0].Transform.InstanceId).IsEqualTo(42u);

        // An accepted player can still be loading. Losing the other invitations does not abandon this match.
        foreach (var player in roster.Skip(1))
            _queue.WithdrawFromBattlefield(player);
        var substitute = AddApplicant(teamTotal + 1);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, substitute);
        _queue.BattlefieldTick(tick);
        await Assert.That(substitute.CurrentInstantGame).IsSameReferenceAs(match);
        _queue.WithdrawFromBattlefield(substitute);
        _queue.WithdrawFromBattlefield(roster[0]);
        await Assert.That(_worlds.GetWorlds().Single().Id).IsEqualTo(42u);
        await match.EndGame();
        await Assert.That(_worlds.GetWorlds().Single().Id).IsEqualTo(42u);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, substitute);
        _queue.BattlefieldTick(tick);
        await Assert.That(substitute.CurrentInstantGame).IsNull();
    }

    [Test]
    [Arguments(InstantGameType.DrillCamp, 10, false)]
    [Arguments(InstantGameType.DrillCamp, 10, true)]
    [Arguments(InstantGameType.Gladiator, 2, false)]
    [Arguments(InstantGameType.Gladiator, 2, true)]
    public async Task MatchWithAResult_DoesNotRefillDuringItsEndingMinute(InstantGameType mode, int teamTotal, bool vacancyBeforeResult)
    {
        var roster = Enumerable.Range(1, teamTotal).Select(AddApplicant).ToArray();
        var observers = roster.Select(Observe).ToArray();
        var rules = BattlefieldGameData.Instance.GetBattlefield((uint)mode).RuleSet;
        rules.TimeEnding = 1;
        foreach (var player in roster)
            _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, player);
        var tick = TimeSpan.FromSeconds(15);
        _queue.BattlefieldTick(tick);
        var match = roster[0].CurrentInstantGame;
        var clock = new FakeTimeProvider();
        Set(match, "_clock", clock);
        for (var i = 0; i < roster.Length; i++)
        {
            var invitation = Messages(observers[i], SCOffsets.SCInviteToInstantGamePacket).Single();
            var faction = (InstantCorps)invitation[20] == InstantCorps.Corps1 ? rules.Corps1FactionId : rules.Corps2FactionId;
            var player = roster[i];
            player.Faction = new() { Id = (FactionsEnum)faction };
            player.OriginFaction = player.Faction;
            match.PlayerInviteResponse(player, true, 1);
            match.OnEnterWorld(player, 1);
        }
        await Assert.That(Messages(observers[0], SCOffsets.SCInstantGameStartPacket).Count()).IsEqualTo(1);

        _queue.WithdrawFromBattlefield(roster[0]);
        var pending = AddApplicant(teamTotal + 1);
        var pendingObserver = Observe(pending);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, pending);
        _queue.BattlefieldTick(tick);
        await Assert.That(pending.CurrentInstantGame).IsSameReferenceAs(match);
        if (vacancyBeforeResult)
            _queue.WithdrawFromBattlefield(roster[1]);

        var teardown = match.EndGame();
        await Assert.That(Messages(pendingObserver, SCOffsets.SCInstantGameEndPacket).Count()).IsEqualTo(1);
        await Assert.That(teardown.IsCompleted).IsFalse();
        var fresh = AddApplicant(teamTotal + 2);
        var freshObserver = Observe(fresh);
        _queue.ApplyToBattlefield((uint)mode, InstantCorps.Any, fresh);
        if (vacancyBeforeResult)
        {
            _queue.BattlefieldTick(tick);
            await Assert.That(Messages(freshObserver, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(0);
        }
        _worlds.TryRemoveCharacter(pending.ObjId);
        _queue.BattlefieldTick(tick);
        await Assert.That(Messages(freshObserver, SCOffsets.SCInviteToInstantGamePacket).Count()).IsEqualTo(0);
        await Assert.That(fresh.CurrentInstantGame).IsNull();
        await Assert.That(_worlds.GetWorlds().Single().Id).IsEqualTo(42u);

        clock.Advance(TimeSpan.FromMinutes(1));
        await teardown;
        await Assert.That(_worlds.GetWorlds().Single().Id).IsEqualTo(42u);
    }

    [Test]
    [Arguments(7u, 40)]
    [Arguments(11u, 50)]
    public async Task StockLevelLimit_RejectsOneLevelBelowAndAdmitsAtTheLimit(uint mode, int minimum)
    {
        LoadStockFields();
        var player = AddApplicant(1);
        var observer = Observe(player);
        player.Level = (byte)(minimum - 1);
        _queue.ApplyToBattlefield(mode, InstantCorps.Any, player);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(0);
        await Assert.That(Messages(observer, SCOffsets.SCErrorMsgPacket)
            .Any(packet => BitConverter.ToInt16(packet, 8) == (short)ErrorMessageType.InstantGameBadLevel)).IsTrue();

        player.Level = (byte)minimum;
        _queue.ApplyToBattlefield(mode, InstantCorps.Any, player);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
        _queue.WithdrawFromBattlefield(player);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StockLevelLimit_RechecksAtMatchmakingAndAcceptance(bool afterInvitation)
    {
        LoadStockFields();
        var player = AddApplicant(1);
        var other = AddApplicant(2);
        var observer = Observe(player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, other);
        if (afterInvitation)
        {
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            var match = player.CurrentInstantGame;
            await Assert.That(match).IsNotNull();
            player.Level = 49;
            match.PlayerInviteResponse(player, true, 1);
        }
        else
        {
            player.Level = 49;
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            await Assert.That(other.CurrentInstantGame).IsNull();
        }

        await Assert.That(player.CurrentInstantGame).IsNull();
        await Assert.That(Messages(observer, SCOffsets.SCLoadInstancePacket).Count()).IsEqualTo(0);
        await Assert.That(Messages(observer, SCOffsets.SCErrorMsgPacket)
            .Any(packet => BitConverter.ToInt16(packet, 8) == (short)ErrorMessageType.InstantGameBadLevel)).IsTrue();
        player.Level = 50;
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        await Assert.That(Messages(observer, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task EntryConditions_StockTagSeparatesTheWornLumberPackAndGlider()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,i.buff_id,t.tag_id FROM items i JOIN item_backpacks p ON p.item_id=i.id
            LEFT JOIN tagged_buffs t ON t.buff_id=i.buff_id AND t.tag_id=1253
            WHERE i.id IN (17683,14677) ORDER BY i.id
            """;
        using var rows = command.ExecuteReader();
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That((rows.GetInt32(0), rows.GetInt32(1), rows.IsDBNull(2))).IsEqualTo((14677, 2224, true));
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That((rows.GetInt32(0), rows.GetInt32(1), rows.GetInt32(2))).IsEqualTo((17683, 1454, 1253));
        await Assert.That(rows.Read()).IsFalse();
    }

    public enum AdmissionStage { Application, Matchmaking, Invitation }

    [Test]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByBuff, 1454u, AdmissionStage.Application)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByBuff, 1454u, AdmissionStage.Matchmaking)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByBuff, 1454u, AdmissionStage.Invitation)]
    [Arguments(ErrorMessageType.RunawayCantApplyBattleField, 5529u, AdmissionStage.Application)]
    [Arguments(ErrorMessageType.RunawayCantApplyBattleField, 5529u, AdmissionStage.Invitation)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByBuff, 6117u, AdmissionStage.Application)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByBuff, 6117u, AdmissionStage.Invitation)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByNoSeamless, 0u, AdmissionStage.Application)]
    [Arguments(ErrorMessageType.InstantGameCannotJoinByNoSeamless, 0u, AdmissionStage.Invitation)]
    public async Task EntryConditions_RefuseOnlyWhileTheStockRestrictionIsActive(ErrorMessageType error, uint buff, AdmissionStage stage)
    {
        var player = AddApplicant(1);
        var other = AddApplicant(2);
        var session = Observe(player);
        if (stage != AdmissionStage.Application)
        {
            _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
            _queue.ApplyToBattlefield(11, InstantCorps.Any, other);
        }
        if (stage == AdmissionStage.Invitation)
        {
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
            await Assert.That(player.CurrentInstantGame).IsNotNull();
        }

        if (buff == 0)
        {
            var world = new WorldInstance(new WorldTemplate { ZoneKeys = [199] }, 0, true, 94);
            GC.SuppressFinalize(world);
            _instances[world.Id] = world;
            _ = new Dungeon(new IndunZone(), world);
            player.ParentWorld = world;
        }
        else
        {
            var status = Mock.Of<IBuffs>();
            status.CheckBuff(buff).Returns(true);
            status.CheckBuffTag(1253u).Returns(buff == 1454);
            player.Buffs = status.Object;
        }

        if (stage == AdmissionStage.Application)
            _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        else if (stage == AdmissionStage.Matchmaking)
            _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        else
            player.CurrentInstantGame.PlayerInviteResponse(player, true, 1);

        await Assert.That(Messages(session, SCOffsets.SCErrorMsgPacket)
            .Count(bytes => BitConverter.ToInt16(bytes, 8) == (short)error)).IsEqualTo(1);
        await Assert.That(player.CurrentInstantGame).IsNull();
        await Assert.That(Messages(session, SCOffsets.SCLoadInstancePacket).Count()).IsEqualTo(0);
        player.ParentWorld = null;
        player.Buffs = Mock.Of<IBuffs>().Object;
        session.Packets.Clear();
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        await Assert.That(Messages(session, SCOffsets.SCAppliedToInstantGamePacket).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task EntryConditions_AllowUntaggedGliderAndRespawnEffectsInANonDungeonWorld()
    {
        var player = AddApplicant(1);
        var other = AddApplicant(2);
        var session = Observe(player);
        var status = Mock.Of<IBuffs>();
        status.CheckBuff(2224u).Returns(true); // Experimental Glider 14677
        status.CheckBuff(2385u).Returns(true); // Rebirth Trauma
        player.Buffs = status.Object;
        var world = new WorldInstance(new WorldTemplate { ZoneKeys = [200] }, 1, true, 93);
        GC.SuppressFinalize(world);
        _instances[world.Id] = world;
        player.ParentWorld = world;
        player.Equipment = new EquipmentContainer(player.Id, SlotType.Equipment, false, player);
        player.Equipment.Items.Add(new Item
        {
            Id = 43, TemplateId = 14677, Count = 1, OwnerId = player.Id, SlotType = SlotType.Equipment,
            Slot = (int)EquipmentItemSlot.Backpack, _holdingContainer = player.Equipment
        });
        await Assert.That(player.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack).TemplateId).IsEqualTo(14677u);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, player);
        _queue.ApplyToBattlefield(11, InstantCorps.Any, other);
        _queue.BattlefieldTick(TimeSpan.FromSeconds(15));
        var match = player.CurrentInstantGame;
        await Assert.That(match).IsNotNull();
        match.PlayerInviteResponse(player, true, 1);
        await Assert.That(Messages(session, SCOffsets.SCLoadInstancePacket).Count()).IsEqualTo(1);
        await Assert.That(Messages(session, SCOffsets.SCErrorMsgPacket).Count()).IsEqualTo(0);
    }

    private static BattlefieldGameData LoadStockFields()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var fields = BattlefieldGameData.Instance;
        fields.Load(connection);
        return fields;
    }

    private static TransferVisibilityTests.RecordingSession Observe(CharacterMock character)
    {
        var session = new TransferVisibilityTests.RecordingSession();
        character.Connection = new GameConnection(session) { ActiveChar = character };
        return session;
    }

    private static IEnumerable<byte[]> Messages(TransferVisibilityTests.RecordingSession session, ushort type) =>
        session.Packets.Where(bytes => bytes.Length >= 8 && BitConverter.ToUInt16(bytes, 6) == type);

    private CharacterMock AddApplicant(int id) => NewSession(id, (uint)id);

    private CharacterMock NewSession(int id, uint objectId)
    {
        var player = new CharacterMock { Id = (uint)id, ObjId = objectId, Level = 50, Hp = 100, Name = $"Player{id}", Buffs = Mock.Of<IBuffs>().Object };
        _worlds.TryAddCharacter(player);
        return player;
    }

    private static void Set<T>(T target, string name, object value) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _originals.Add((field, field.GetValue(null)));
        field.SetValue(null, manager);
    }
}
