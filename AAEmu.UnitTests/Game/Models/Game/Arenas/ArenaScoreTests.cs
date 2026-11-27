using System.Collections.Concurrent;
using System.Reflection;
using System.Numerics;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Models.Game.Arenas;

[NotInParallel]
public sealed class ArenaScoreTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<(FieldInfo Field, object Original)> _originals = [];
    private readonly Dictionary<ulong, ItemContainer> _containers = [];
    private readonly TransferVisibilityTests.RecordingSession _observer = new();
    private readonly WorldInstance _world = new(new WorldTemplate(), 0, true, 42);
    private readonly List<Character> _red = [];
    private readonly List<Character> _blue = [];
    private readonly BattlefieldGameData _fields = new();
    private WorldConfig _previousWorld;
    private bool _previousDebugInfo;
    private InstantGame _game;
    private readonly List<GladiatorRankResult> _rankResults = [];
    private readonly List<ArenaMatchResult> _matchResults = [];
    private Task _rankSaved = Task.CompletedTask;

    [Before(Test)]
    public void SetUp()
    {
        GC.SuppressFinalize(_world);
        _world.MateManager = new MateManager(_world);
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        AppConfiguration.Instance.DebugInfo = false;
        _previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { MinimumExpLossLevel = 60 };

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = """
            CREATE TABLE battle_fields(id INTEGER, zone_key INTEGER);
            INSERT INTO battle_fields VALUES(7,198),(11,302);
            CREATE TABLE game_rule_sets(id INTEGER, battle_field_id INTEGER, corps_size INTEGER, corps1_id INTEGER,
                corps2_id INTEGER, time_opening INTEGER, time_ending INTEGER, time_playing INTEGER, victory_score INTEGER, level_min INTEGER, time_resurrection_delay INTEGER,
                rank_win_point INTEGER, rank_lose_point INTEGER, rank_draw_point INTEGER,
                bonus_winner INTEGER, bonus_loser INTEGER, bonus_top_enemy_pc_kill INTEGER, bonus_top_enemy_non_pc_kill INTEGER);
            INSERT INTO game_rule_sets VALUES(22,7,5,159,160,4,1,15,0,40,15,0,0,0,50,10,10,10),
                (28,11,1,159,160,2,1,10,60,50,5,4,1,0,20,5,0,0);
            CREATE TABLE game_score_rules(id INTEGER, rule_set_id INTEGER, rule_set_corps INTEGER,
                event_id INTEGER, event_value INTEGER, event_score INTEGER);
            INSERT INTO game_score_rules VALUES(68,22,-1,200,0,3),(93,28,-1,200,0,30),
                (67,22,-1,101,2,0),(84,22,-1,202,0,2),(85,22,-1,203,0,2),(86,22,-1,204,0,2);
            CREATE TABLE resurrection_waiting_times(id INTEGER, penalty_duration INTEGER, waiting_time INTEGER, siege_waiting_time INTEGER);
            INSERT INTO resurrection_waiting_times VALUES(1,600,0,0);
            """;
        sql.ExecuteNonQuery();
        _fields.Load(db);
        var deaths = new ResurrectionGameData();
        deaths.Load(db);
        Install(deaths);
        Install(new DuelManager());
        Install(new PriestBuffGameData());
        Install(new ExperienceManager());
        var worlds = new WorldManager(null, null, null, null, null);
        Set(worlds, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [_world.Id] = _world });
        Install(worlds);
        var zones = new ZoneManager(null, null);
        Set(zones, "_zones", new Dictionary<uint, Zone>());
        Install(zones);
        var skills = new SkillManager(null, null);
        Set(skills, "_taggedSkills", new Dictionary<uint, List<uint>>());
        Install(skills);
        var items = new ItemManager(null, null, null, null, null, null);
        Set(items, "_config", new ItemConfig());
        Set(items, "_allPersistentContainers", _containers);
        Install(items);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var player in _red.Concat(_blue))
            MovementValidation.Reset(player.ObjId);
        AppConfiguration.Instance.World = _previousWorld;
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        foreach (var (field, original) in _originals)
            field.SetValue(null, original);
    }

    [Test]
    public async Task Gladiator_EntryCompletionRunsOncePerMember()
    {
        Start(11);
        var player = _red[0];
        _game.OnEnterWorld(player, 0);
        Kill(player, _blue[0]);

        await Assert.That(Packets(SCOffsets.SCInstantGameJoinedPacket).Length).IsEqualTo(1);
        await Assert.That(Packets(SCOffsets.SCInstantGameStartPacket).Length).IsEqualTo(1);
        var points = ReadPoints(Packets(SCOffsets.SCInstantGameAddPointPacket).Single());
        await Assert.That(points).IsEqualTo(((byte)0, 30, 30, 0, player.Name));
        await Assert.That(_matchResults).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Gladiator_TwoEnemyKillsReachSixty_OneResultForEitherWinningTeam(bool blueWins)
    {
        Start(11);
        var winner = blueWins ? _blue[0] : _red[0];
        var loser = blueWins ? _red[0] : _blue[0];
        Kill(winner, loser);
        Kill(loser, winner);
        Kill(winner, loser);
        var points = Packets(SCOffsets.SCInstantGameAddPointPacket).Select(ReadPoints).ToArray();
        await Assert.That(points.Length).IsEqualTo(3);
        await Assert.That(points[0]).IsEqualTo(((byte)(blueWins ? 1 : 0), 30, blueWins ? 0 : 30, blueWins ? 30 : 0, winner.Name));
        await Assert.That(points[1]).IsEqualTo(((byte)(blueWins ? 0 : 1), 30, 30, 30, loser.Name));
        await Assert.That(points[2]).IsEqualTo(((byte)(blueWins ? 1 : 0), 30, blueWins ? 30 : 60, blueWins ? 60 : 30, winner.Name));

        Kill(loser, winner);
        await _game.EndGame(); // A timeout notification after the winning kill cannot finish a second time.
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(3);
        var killTotals = Packets(SCOffsets.SCInstantGameKillPacket).Select(ReadKillTotals).ToArray();
        await Assert.That(killTotals).IsEquivalentTo([(1, 1), (1, 1), (2, 2)]);
        var end = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        await Assert.That(end.ReadByte()).IsEqualTo((byte)4);
        await CheckTeam(end, blueWins ? 1 : 2, 159, blueWins ? 30 : 60, blueWins ? 1 : 2, blueWins ? 2 : 1);
        await CheckTeam(end, blueWins ? 2 : 1, 160, blueWins ? 60 : 30, blueWins ? 2 : 1, blueWins ? 1 : 2);
        await Assert.That(end.Pos).IsEqualTo(end.Count);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(winner.CurrentInstantGame).IsNull();
        await Assert.That(loser.CurrentInstantGame).IsNull();
        await Assert.That(winner.Events.OnKill.GetInvocationList().Any(handler => handler.Target == _game)).IsFalse();
        await Assert.That(loser.Events.OnKill.GetInvocationList().Any(handler => handler.Target == _game)).IsFalse();
        await Assert.That(Packets(SCOffsets.SCLoadInstancePacket).Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DrillCamp_DamageAssistsDecideTheTimeoutForEitherTeam(bool blueWins)
    {
        Start(7);
        var (winners, losers) = blueWins ? (_blue, _red) : (_red, _blue);
        var friends = new FriendMananger();
        Set(friends, "_allFriends", new Dictionary<uint, FriendTemplate>());
        Install(friends);
        foreach (var player in _red.Concat(_blue))
            player.IsOnline = true;
        var killer = winners[0];
        var victim = losers[0];
        Kill(victim, winners[1]);
        Kill(victim, winners[2]);
        _clock.Advance(TimeSpan.FromSeconds(15));

        victim.Hp = 100;
        victim.ReduceCurrentHp(winners[1], 1, KillReason.PvpBattleField);
        victim.ReduceCurrentHp(winners[2], 15, KillReason.PvpBattleField);
        victim.ReduceCurrentHp(winners[2], 30, KillReason.PvpBattleField);
        victim.ReduceCurrentHp(losers[1], 2, KillReason.PvpBattleField);
        victim.ReduceCurrentHp(winners[4], 2, KillReason.PvpBattleField);
        _game.LeaveInstantGame(winners[4]);
        killer.RecordPvpHealFrom(winners[2]);
        killer.RecordPvpHealFrom(winners[3]);
        victim.ReduceCurrentHp(killer, 1, KillReason.PvpBattleField);
        var credited = victim.CollectAssists(killer);
        await Assert.That(credited.SetEquals([winners[1].Id, winners[2].Id, winners[3].Id, winners[4].Id, losers[1].Id])).IsTrue();
        victim.ReduceCurrentHp(killer, victim.Hp, KillReason.PvpBattleField);
        victim.ReduceCurrentHp(killer, 1, KillReason.PvpBattleField);

        var points = Packets(SCOffsets.SCInstantGameAddPointPacket).Select(ReadPoints).ToArray();
        await Assert.That(points.Length).IsEqualTo(5);
        var corps = (byte)(blueWins ? InstantCorps.Corps2 : InstantCorps.Corps1);
        await Assert.That(points.Where(p => p.Corps == corps).Select(p => (p.Scorer, p.Award)).ToArray())
            .IsEquivalentTo([(killer.Name, 3), (winners[1].Name, 2), (winners[2].Name, 2)]);
        await Assert.That((points[^1].Red, points[^1].Blue)).IsEqualTo(blueWins ? (6, 7) : (7, 6));

        _clock.Advance(TimeSpan.FromMinutes(15));
        await _game.EndGame();
        var end = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        await Assert.That(end.ReadByte()).IsEqualTo((byte)BattlefieldEndingReason.Timeout);
        var results = new Dictionary<uint, (int Score, ushort Assists)>
        {
            [killer.Id] = (3, 0),
            [winners[1].Id] = (2, 1),
            [winners[2].Id] = (2, 1),
            [victim.Id] = (6, 0)
        };
        await CheckTeam(end, blueWins ? 1 : 2, 159, blueWins ? 6 : 7, blueWins ? 2 : 1, blueWins ? 1 : 2, results);
        await CheckTeam(end, blueWins ? 2 : 1, 160, blueWins ? 7 : 6, blueWins ? 1 : 2, blueWins ? 2 : 1, results);
        await Assert.That(end.Pos).IsEqualTo(end.Count);
    }

    [Test]
    [Arguments(7u, 15)]
    [Arguments(11u, 5)]
    public async Task Resurrection_UsesTheStockDelayAfterAnEnemyKill(uint id, int seconds)
    {
        Start(id);
        var victim = _red[0];
        var killer = _blue[0];
        Kill(killer, victim);
        killer.Hp = 17;
        _clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        await Assert.That(victim.Hp).IsEqualTo(0);
        await Assert.That(killer.Hp).IsEqualTo(17);
        _clock.Advance(TimeSpan.FromTicks(1));
        await Assert.That(victim.Hp).IsEqualTo(victim.MaxHp);
        await Assert.That(killer.Hp).IsEqualTo(id == 11 ? killer.MaxHp : 17);
    }

    [Test]
    [Arguments(7u, false)]
    [Arguments(7u, true)]
    [Arguments(11u, false)]
    [Arguments(11u, true)]
    public async Task Resurrection_PublishesTheTeamSpawnAndAcceptsTheNextStep(uint fieldId, bool blueVictim)
    {
        Start(fieldId);
        var field = _fields.GetBattlefield(fieldId);
        var victim = blueVictim ? _blue[0] : _red[0];
        var killer = blueVictim ? _red[0] : _blue[0];
        var spawn = blueVictim ? field.Spawns.Corps2Spawn : field.Spawns.Corps1Spawn;
        var opposite = blueVictim ? field.Spawns.Corps1Spawn : field.Spawns.Corps2Spawn;
        var arrival = new Vector3(spawn.X, spawn.Y, spawn.Z);
        var killerArrival = new Vector3(opposite.X, opposite.Y, opposite.Z);
        var midpoint = (arrival + killerArrival) / 2;
        var attack = midpoint + Vector3.UnitY * 2;

        _world.Template.CellX = 2;
        _world.Template.CellY = 1;
        _world.Template.ZoneKeyByRegions = new uint[32, 16];
        _world.Regions = new Region[32, 16];
        for (var i = 0; i < _world.Regions.GetLength(0); i++)
            for (var j = 0; j < _world.Regions.GetLength(1); j++)
                _world.Regions[i, j] = new Region(_world, i, j, field.ZoneKey);
        Install(new SusManager(WorldManager.Instance));
        var otherView = new TransferVisibilityTests.RecordingSession();
        foreach (var player in new[] { victim, killer })
        {
            player.Transform.InstanceId = _world.Id;
            player.IsVisible = true;
            player.Mates = new CharacterMates(player);
            player.VisualOptions = new() { Stp = new byte[6] };
            player.Equipment.ContainerSize = 28;
            player.Appellations = new CharacterAppellations(player);
            player.Skills = new CharacterSkills(player);
            player.Abilities = new CharacterAbilities(player);
            player.Connection = new GameConnection(player == victim ? _observer : otherView) { ActiveChar = player };
            _world.AddObject(player);
            var site = player == victim ? midpoint : attack;
            player.Transform.Local.SetPosition(site);
            WorldManager.Instance.AddVisibleObject(player);
            MovementValidation.RecordServerRelocation(player);
        }

        Kill(killer, victim);
        _clock.Advance(field.RuleSet.ResurrectionDelay - TimeSpan.FromTicks(1));
        await Assert.That(victim.Hp).IsZero();
        await Assert.That(victim.Transform.World.Position).IsEqualTo(midpoint);
        _clock.Advance(TimeSpan.FromTicks(1));

        await Assert.That(victim.Transform.World.Position).IsEqualTo(arrival);
        await Assert.That(victim.Region).IsSameReferenceAs(_world.GetRegionByPos(arrival));
        foreach (var view in new[] { _observer, otherView })
        {
            var notice = Packets(SCOffsets.SCCharacterResurrectedPacket, 8, view).Single();
            await Assert.That(notice.ReadBc()).IsEqualTo(victim.ObjId);
            var (x, y, z) = notice.ReadPosition();
            await Assert.That(Vector3.Distance(new(x, y, z), arrival)).IsLessThan(.001f);
        }

        var killerAt = fieldId == 11 ? killerArrival : attack;
        await Assert.That(killer.Transform.World.Position).IsEqualTo(killerAt);
        await Assert.That(killer.Region).IsSameReferenceAs(_world.GetRegionByPos(killerAt));
        var teleports = Packets(SCOffsets.SCTeleportUnitPacket, 8, otherView);
        await Assert.That(teleports.Length).IsEqualTo(fieldId == 11 ? 1 : 0);
        if (fieldId == 11)
        {
            teleports[0].ReadByte();
            teleports[0].ReadInt16();
            var (kx, ky, kz) = teleports[0].ReadPosition();
            await Assert.That(Vector3.Distance(new(kx, ky, kz), killerArrival)).IsLessThan(.001f);
            // The stock Gladiator spawns are outside each other's visible regions.
            await Assert.That(RemovedFromView(_observer, killer.ObjId)).IsTrue();
            await Assert.That(RemovedFromView(otherView, victim.ObjId)).IsTrue();
        }
        else
        {
            // Both Drill Camp spawns remain visible from the fight at the midpoint.
            await Assert.That(RemovedFromView(_observer, killer.ObjId)).IsFalse();
            await Assert.That(RemovedFromView(otherView, victim.ObjId)).IsFalse();
        }

        foreach (var player in new[] { victim, killer })
        {
            var step = player.Transform.World.Position + Vector3.UnitX;
            var stream = new PacketStream().WriteBc(player.ObjId).Write((byte)MoveTypeEnum.Unit);
            new UnitMoveType { X = step.X, Y = step.Y, Z = step.Z, DeltaMovement = new sbyte[3] }.Write(stream);
            stream.Rollback();
            var move = new CSMoveUnitPacket { Connection = player.Connection };
            move.Read(stream);
            move.Execute();
            var (xStep, yStep, zStep) = Helpers.ConvertPosition(Helpers.ConvertPosition(step.X, step.Y, step.Z));
            await Assert.That(player.Transform.World.Position).IsEqualTo(new Vector3(xStep, yStep, zStep));
            await Assert.That(player.DisabledSetPosition).IsFalse();
        }
    }

    [Test]
    [Arguments(7u)]
    [Arguments(11u)]
    public async Task Resurrection_OnlyAMovedWinnerStartsANewFall(uint fieldId)
    {
        Start(fieldId);
        Install(new PermissionManager(null));
        var field = _fields.GetBattlefield(fieldId);
        var winner = _blue[0];
        var victim = _red[0];
        var spawn = field.Spawns.Corps2Spawn;
        var landing = new Vector3(spawn.X + 10, spawn.Y, spawn.Z);
        winner.Hp = winner.MaxHp;
        Kill(winner, victim);

        // The winner is already falling when the other player revives.
        winner.Transform.Local.SetPosition(landing + Vector3.UnitZ * 24);
        winner.ObserveFallMovement(false, 0);
        winner.Transform.Local.SetPosition(landing + Vector3.UnitZ * 12);
        winner.ObserveFallMovement(false, .4);
        _clock.Advance(field.RuleSet.ResurrectionDelay);

        if (fieldId == 7)
            winner.Transform.Local.SetPosition(landing);
        winner.ObserveFallMovement(true, .8);
        if (fieldId == 11)
            await Assert.That(winner.Hp).IsEqualTo(winner.MaxHp);
        else
            await Assert.That(winner.Hp).IsLessThan(winner.MaxHp);
    }

    [Test]
    public async Task Resurrection_FinishedMatchKeepsBothPositions()
    {
        Start(11);
        var victim = _red[0];
        var killer = _blue[0];
        var deathSite = new Vector3(575, 506, 266);
        var attackSite = deathSite + Vector3.UnitY;
        victim.Transform.Local.SetPosition(deathSite);
        killer.Transform.Local.SetPosition(attackSite);
        Kill(killer, victim);
        var finish = _game.EndGame();
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(victim.Hp).IsZero();
        await Assert.That(victim.Transform.World.Position).IsEqualTo(deathSite);
        await Assert.That(killer.Transform.World.Position).IsEqualTo(attackSite);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await finish;
    }

    [Test]
    public async Task Resurrection_LeavesADepartedPlayerAloneAtTheDeadline()
    {
        Start(11);
        var victim = _red[0];
        Kill(_blue[0], victim);
        _game.LeaveInstantGame(victim);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(victim.Hp).IsEqualTo(0);
        await Assert.That(victim.CurrentInstantGame).IsNull();
    }

    [Test]
    public async Task DisconnectedScorerKeepsEarnedResultUntilNormalEnd()
    {
        Start(11);
        var departed = _blue[0];
        var other = _red[0];
        var worlds = WorldManager.Instance;
        worlds.TryAddCharacter(departed);
        Kill(departed, other);
        worlds.TryRemoveCharacter(departed.ObjId);

        await Assert.That(departed.CurrentInstantGame).IsNull();
        await Assert.That(departed.Events.OnKill.GetInvocationList().Any(handler => handler.Target == _game)).IsFalse();
        var teardown = _game.EndGame();
        var rank = _rankResults.Single();
        await Assert.That((rank.Corps1CharacterId, rank.Corps1RankPoints, rank.Corps2CharacterId, rank.Corps2RankPoints))
            .IsEqualTo((other.Id, 1, departed.Id, 4));
        var result = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        result.ReadByte();
        await CheckTeam(result, 1, 159, 0, 0, 1);
        await CheckTeam(result, 2, 160, 30, 1, 0);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await teardown;
        await Assert.That(other.CurrentInstantGame).IsNull();
    }

    [Test]
    [Arguments(7u, 1, 2)]
    [Arguments(7u, 2, 1)]
    [Arguments(7u, 1, 1)]
    [Arguments(11u, 0, 0)]
    [Arguments(11u, 0, 1)]
    public async Task TimeLimit_EndsOnceWithBothTeamResults(uint id, int redKills, int blueKills)
    {
        Start(id);
        for (var i = 0; i < redKills; i++)
            Kill(_red[i % _red.Count], _blue[0]);
        for (var i = 0; i < blueKills; i++)
            Kill(_blue[i % _blue.Count], _red[0]);

        int value = id == 7 ? 3 : 30;
        int redScore = value * redKills, blueScore = value * blueKills;
        var points = Packets(SCOffsets.SCInstantGameAddPointPacket).Select(ReadPoints).ToArray();
        await Assert.That(points.Length).IsEqualTo(redKills + blueKills);
        if (points.Length > 0)
            await Assert.That((points[^1].Red, points[^1].Blue)).IsEqualTo((redScore, blueScore));

        _clock.Advance(TimeSpan.FromMinutes(id == 7 ? 15 : 10) - TimeSpan.FromTicks(1));
        await Assert.That(Packets(SCOffsets.SCInstantGameEndPacket).Length).IsEqualTo(0);
        _clock.Advance(TimeSpan.FromTicks(1));
        if (id == 7)
            await Assert.That(_rankResults).IsEmpty();
        else
        {
            var rank = _rankResults.Single();
            await Assert.That((rank.Corps1Score, rank.Corps2Score)).IsEqualTo((redScore, blueScore));
            await Assert.That((rank.Corps1RankPoints, rank.Corps2RankPoints)).IsEqualTo(blueScore > redScore ? (1, 4) : (0, 0));
        }
        var end = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        await Assert.That(end.ReadByte()).IsEqualTo((byte)1);
        await CheckTeam(end, redScore > blueScore ? 2 : redScore < blueScore ? 1 : 3, 159, redScore, redKills, blueKills);
        await CheckTeam(end, blueScore > redScore ? 2 : blueScore < redScore ? 1 : 3, 160, blueScore, blueKills, redKills);
        await Assert.That(end.Pos).IsEqualTo(end.Count);
        Kill(_red[0], _blue[0]);
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(points.Length);
        await Assert.That(Packets(SCOffsets.SCInstantGameEndPacket).Length).IsEqualTo(1);
    }

    [Test]
    public async Task AdvertisedStart_OpensTheScoreWindowAfterTheCountdown()
    {
        Start(11, false);
        var timing = Packets(SCOffsets.SCInstantGameStartPacket).Single();
        var sent = timing.ReadUInt32();
        var start = timing.ReadUInt32();
        await Assert.That(start - sent).IsEqualTo(20_000u);

        _clock.Advance(TimeSpan.FromMilliseconds(start - sent) - TimeSpan.FromTicks(1));
        Kill(_blue[0], _red[0]);
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(0);
        await Assert.That(Packets(SCOffsets.SCInstantGameKillPacket).Length).IsEqualTo(0);
        _clock.Advance(TimeSpan.FromTicks(2));
        Kill(_blue[0], _red[0]);
        var point = ReadPoints(Packets(SCOffsets.SCInstantGameAddPointPacket).Single());
        await Assert.That((point.Red, point.Blue)).IsEqualTo((0, 30));
    }

    [Test]
    public async Task KillAtDeadline_TakesTheTimeoutResultOnceEvenIfTimerWaitsForTheLock()
    {
        Start(11);
        var started = _clock.GetTimestamp();
        _clock.Advance(TimeSpan.FromSeconds(3));
        Task timeout;
        lock (SaveManager.PersistenceSyncRoot)
        {
            timeout = Task.Run(() => _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(3)));
            if (!SpinWait.SpinUntil(() => _clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The match clock did not reach its deadline.");
            Kill(_red[0], _blue[0]);
        }
        await timeout;
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(0);
        var end = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        await Assert.That(end.ReadByte()).IsEqualTo((byte)1);
        await CheckTeam(end, 3, 159, 0, 0, 0);
        await CheckTeam(end, 3, 160, 0, 0, 0);
    }

    [Test]
    public async Task EnemyFinalHit_ExcludesSelfAlliesAndNonmembers()
    {
        Start(7);
        var outsider = new CharacterMock();
        var red = _red[0];
        foreach (var victim in new[] { red, _red[1], outsider })
            red.Events.OnKill(victim, new OnKillArgs { Killer = red, Victim = victim });
        _game.OnKill(_blue[0], new OnKillArgs { Killer = outsider, Victim = _blue[0] });
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(0);
        await Assert.That(Packets(SCOffsets.SCInstantGameKillPacket).Length).IsEqualTo(0);
    }

    [Test]
    public async Task StockCompact_UsesTheFinalEnemyHitterRuleForBothPlayableArenas()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        _fields.Load(connection);
        var camp = _fields.GetBattlefield(7).RuleSet;
        var duel = _fields.GetBattlefield(11).RuleSet;
        await Assert.That((camp.EnemyPlayerDamageAssistScore, duel.EnemyPlayerDamageAssistScore)).IsEqualTo((2, 0));
        await Assert.That(camp.ResurrectionDelay).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(duel.ResurrectionDelay).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That((camp.RankWinPoints, camp.RankLossPoints, camp.RankDrawPoints)).IsEqualTo((0, 0, 0));
        await Assert.That((duel.RankWinPoints, duel.RankLossPoints, duel.RankDrawPoints)).IsEqualTo((4, 1, 0));
        await Assert.That((camp.Id, camp.EnemyPlayerKillScore, camp.VictoryScore, camp.TimePlaying)).IsEqualTo((22u, 3, 0, 15));
        await Assert.That((duel.Id, duel.EnemyPlayerKillScore, duel.VictoryScore, duel.TimePlaying)).IsEqualTo((28u, 30, 60, 10));
    }

    [Test]
    [Arguments(VictoryState.Invalid, 14u)]
    [Arguments(VictoryState.Draw, 14u)]
    [Arguments(VictoryState.Win, 15u)]
    [Arguments(VictoryState.Lose, 30u)]
    public async Task MemberResult_UsesOnlyTheFinalOutcomeAndKeepsOtherAchievements(VictoryState state, uint expected)
    {
        var member = new InstantGameTeamMember
        {
            Character = new ArenaPlayer { Id = 1, Name = "Player" },
            Corps = new InstantGameTeamResult(state, 159),
            BonusSet = 0x1f
        };
        var stream = member.Write(new PacketStream());
        stream.Pos = 0;
        stream.ReadUInt32();
        stream.ReadString();
        stream.ReadInt32();
        stream.ReadInt32();
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expected);
    }

    [Test]
    public async Task OpenMatch_HasNoFinalTeamOutcome()
    {
        Start(11);
        var members = (Dictionary<Character, InstantGameTeamMember>)typeof(InstantGame)
            .GetField("_members", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_game)!;
        foreach (var member in members.Values)
            await Assert.That(member.Corps.State).IsEqualTo(VictoryState.Invalid);
        await Assert.That(Packets(SCOffsets.SCInstantGameEndPacket).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GladiatorTarget_HoldsBothFinalNoticesUntilTheSameResultCommits(bool corps2Wins)
    {
        var database = new TaskCompletionSource();
        _rankSaved = database.Task;
        Start(11);
        var (winner, loser) = corps2Wins ? (_blue[0], _red[0]) : (_red[0], _blue[0]);
        Kill(winner, loser);
        Kill(winner, loser);
        await Task.WhenAll(_game.EndGame(), Task.Run(_game.EndGame));
        Kill(winner, loser);
        var rank = _rankResults.Single();
        await Assert.That((rank.Corps1RankPoints, rank.Corps2RankPoints)).IsEqualTo(corps2Wins ? (1, 4) : (4, 1));
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(1);
        await Assert.That(Packets(SCOffsets.SCInstantGameEndPacket)).IsEmpty();
        _game.CancelAdmission(_blue[0]);
        database.SetResult();
        await Assert.That(Packets(SCOffsets.SCInstantGameAddPointPacket).Length).IsEqualTo(2);
        var end = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        await Assert.That(end.ReadByte()).IsEqualTo((byte)BattlefieldEndingReason.Score);
        await CheckTeam(end, corps2Wins ? 1 : 2, 159, corps2Wins ? 0 : 60, corps2Wins ? 0 : 2, corps2Wins ? 2 : 0);
        await CheckTeam(end, corps2Wins ? 2 : 1, 160, corps2Wins ? 60 : 0, corps2Wins ? 2 : 0, corps2Wins ? 0 : 2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task GladiatorDepartureAtCombatOpening_UsesTheActualRoster(bool cancelAdmission, bool atStart)
    {
        Start(11, false);
        _clock.Advance(TimeSpan.FromSeconds(20) - (atStart ? TimeSpan.Zero : TimeSpan.FromTicks(1)));
        if (cancelAdmission)
            _game.CancelAdmission(_blue[0]);
        else
            _game.LeaveInstantGame(_blue[0]);
        _clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.That(_game.IsFinished).IsTrue();
        await Assert.That(_rankResults.Count).IsEqualTo(atStart ? 1 : 0);
    }

    [Test]
    public async Task BothPlayersLeaveBeforeTimeout_SavesNoRankResult()
    {
        Start(11);
        Kill(_blue[0], _red[0]);
        _game.LeaveInstantGame(_red[0]);
        _game.LeaveInstantGame(_blue[0]);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.That(_game.IsFinished).IsTrue();
        await Assert.That(_rankResults).IsEmpty();
    }

    [Test]
    public async Task CountdownEndsEarly_SavesNoRankResult()
    {
        Start(11, false);
        var end = _game.EndGame();
        await Assert.That(_rankResults).IsEmpty();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await end;
    }

    [Test]
    [Arguments(7u, false)]
    [Arguments(7u, true)]
    [Arguments(11u, false)]
    [Arguments(11u, true)]
    public async Task CompletedMatch_ShowsHonorForBothSidesAndAllTiedLeaders(uint field, bool draw)
    {
        Start(field);
        if (!draw)
        {
            Kill(_red[0], _blue[0]);
            if (field == 7)
            {
                Kill(_red[1], _blue[1]);
                Kill(_blue[0], _red[0]);
            }
        }
        var end = _game.EndGame();
        var receipt = _matchResults.Single().Awards.ToDictionary(award => award.CharacterId, award => award.Amount);
        var display = ReadFinalAwards();
        await Assert.That(receipt.Count).IsEqualTo(_red.Count + _blue.Count);
        await Assert.That(display.Count).IsEqualTo(receipt.Count);
        foreach (var player in _red.Concat(_blue))
        {
            var won = !draw && _red.Contains(player);
            var honor = field == 11 ? (won ? 20 : 5) : (won ? 50 : 10);
            var badges = draw ? 0u : won ? 1u : 16u;
            if (!draw && field == 7 && (player == _red[0] || player == _red[1] || player == _blue[0]))
            {
                honor += 10;
                badges |= 4u;
            }
            await Assert.That(receipt[player.Id]).IsEqualTo(honor);
            await Assert.That(display[player.Id]).IsEqualTo((honor, badges));
        }
        await _game.EndGame();
        await Assert.That(_matchResults.Count).IsEqualTo(1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await end;
    }

    [Test]
    public async Task DrillCampHonor_CombinesTiedEnemyNpcAndPlayerLeadsAcrossBothTeams()
    {
        Start(7);
        var friendly = new Npc { ParentWorld = _world, Faction = new() { Id = FactionsEnum.Friendly } };
        var hostile = new Npc { ParentWorld = _world, Faction = new() { Id = FactionsEnum.Hostile } };
        var elsewhere = new Npc { Faction = hostile.Faction };
        void NpcKill(Character killer, Npc target) =>
            killer.Events.OnKill(target, new OnKillArgs { Killer = killer, Victim = target });

        Kill(_red[0], _blue[2]);
        Kill(_blue[0], _red[2]);
        NpcKill(_red[0], hostile);
        NpcKill(_blue[1], hostile);
        NpcKill(_blue[0], friendly);
        NpcKill(_red[1], elsewhere);
        var end = _game.EndGame();
        var awards = _matchResults.Single().Awards.ToDictionary(award => award.CharacterId, award => award.Amount);
        await Assert.That(awards[_red[0].Id]).IsEqualTo(30);
        await Assert.That(awards[_blue[0].Id]).IsEqualTo(20);
        await Assert.That(awards[_blue[1].Id]).IsEqualTo(20);
        await Assert.That(awards[_red[1].Id]).IsEqualTo(10);
        await Assert.That(awards.Values.Sum()).IsEqualTo(140);
        var display = ReadFinalAwards();
        await Assert.That(display[_red[0].Id]).IsEqualTo((30, 12u));
        await Assert.That(display[_blue[0].Id]).IsEqualTo((20, 4u));
        await Assert.That(display[_blue[1].Id]).IsEqualTo((20, 8u));
        await Assert.That(display[_red[1].Id]).IsEqualTo((10, 0u));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await end;
    }

    private Dictionary<uint, (int Honor, uint Badges)> ReadFinalAwards()
    {
        var display = new Dictionary<uint, (int Honor, uint Badges)>();
        var packet = Packets(SCOffsets.SCInstantGameEndPacket).Single();
        packet.ReadByte();
        for (var side = 0; side < 2; side++)
        {
            packet.ReadByte();
            packet.ReadUInt32();
            packet.ReadInt32();
            packet.ReadInt16();
            packet.ReadInt16();
            var count = packet.ReadByte();
            for (var i = 0; i < count; i++)
            {
                var id = packet.ReadUInt32();
                packet.ReadString();
                packet.ReadInt32();
                display.Add(id, (packet.ReadInt32(), packet.ReadUInt32()));
                for (var j = 0; j < 4; j++)
                    packet.ReadUInt16();
            }
        }
        return display;
    }

    private void Start(uint id, bool finishCountdown = true)
    {
        var field = _fields.GetBattlefield(id);
        _game = new InstantGame(field, _world, _clock)
        {
            SaveMatchResult = result =>
            {
                _matchResults.Add(result);
                if (result.Rank != null)
                    _rankResults.Add(result.Rank);
                return _rankSaved;
            }
        };
        for (var i = 0; i < field.RuleSet.CorpsSize; i++)
        {
            _red.Add(Join((uint)i + 1, InstantCorps.Corps1, (FactionsEnum)159));
            _blue.Add(Join((uint)i + 11, InstantCorps.Corps2, (FactionsEnum)160));
        }
        if (finishCountdown)
            _clock.Advance(TimeSpan.FromSeconds(20));
    }

    private ArenaPlayer Join(uint id, InstantCorps corps, FactionsEnum faction)
    {
        var character = new ArenaPlayer { Id = id, ObjId = id, Name = $"Player{id}", Level = 50, ParentWorld = _world,
            Faction = new() { Id = faction }, Buffs = Mock.Of<IBuffs>().Object };
        character.OriginFaction = character.Faction;
        character.MainWorldPosition = character.Transform.CloneDetached(character);
        if (id == 1)
            character.Connection = new GameConnection(_observer);
        foreach (var type in Enum.GetValues<SlotType>().Where(t => t != SlotType.EquipmentMate))
        {
            var key = (ulong)_containers.Count + 1;
            _containers.Add(key, new ItemContainer(id, type, false, character) { ContainerId = key });
        }
        character.Inventory = new Inventory(character);
        _game.AddPlayer(character, corps);
        _game.OnEnterWorld(character, 0);
        return character;
    }

    private static void Kill(Character killer, Character victim)
    {
        victim.Hp = 1;
        victim.ReduceCurrentHp(killer, 1, KillReason.PvpBattleField);
    }

    private bool RemovedFromView(TransferVisibilityTests.RecordingSession view, uint id)
    {
        foreach (var removal in Packets(SCOffsets.SCUnitsRemovedPacket, 8, view))
        {
            var count = removal.ReadUInt16();
            for (var i = 0; i < count; i++)
                if (removal.ReadBc() == id)
                    return true;
        }
        return false;
    }

    private PacketStream[] Packets(ushort opcode, int position = 16, TransferVisibilityTests.RecordingSession view = null) => (view ?? _observer).Packets
        .Where(bytes => bytes.Length >= 8 && BitConverter.ToUInt16(bytes, 6) == opcode)
        .Select(bytes => new PacketStream(bytes) { Pos = position }).ToArray();

    private static (byte Corps, int Award, int Red, int Blue, string Scorer) ReadPoints(PacketStream stream) =>
        (stream.ReadByte(), stream.ReadInt32(), stream.ReadInt32(), stream.ReadInt32(), stream.ReadString());

    private static (int KillerTeamKills, int VictimTeamDeaths) ReadKillTotals(PacketStream stream)
    {
        stream.ReadUInt32();
        stream.ReadUInt32();
        stream.ReadByte();
        stream.ReadByte();
        stream.ReadSByte();
        stream.ReadString();
        stream.ReadString();
        return (stream.ReadInt32(), stream.ReadInt32());
    }

    private static async Task CheckTeam(PacketStream stream, int state, uint faction, int score, int kills, int deaths,
        IReadOnlyDictionary<uint, (int Score, ushort Assists)> expectedMembers = null)
    {
        var actual = (stream.ReadByte(), stream.ReadUInt32(), stream.ReadInt32(), stream.ReadInt16(), stream.ReadInt16());
        await Assert.That(actual).IsEqualTo(((byte)state, faction, score, (short)kills, (short)deaths));
        var members = stream.ReadByte();
        var personalScores = 0;
        for (var i = 0; i < members; i++)
        {
            var id = stream.ReadUInt32();
            stream.ReadString();
            var memberScore = stream.ReadInt32();
            personalScores += memberScore;
            stream.ReadInt32(); // The completed match tests check each recipient's Honor.
            var expectedBadge = state switch { 2 => 1u, 1 => 16u, _ => 0u };
            await Assert.That(stream.ReadUInt32() & (1u | 16u)).IsEqualTo(expectedBadge);
            stream.ReadUInt16();
            stream.ReadUInt16();
            var assists = stream.ReadUInt16();
            stream.ReadUInt16();
            if (expectedMembers != null)
                await Assert.That((memberScore, assists)).IsEqualTo(expectedMembers.GetValueOrDefault(id));
        }
        await Assert.That(personalScores).IsEqualTo(score);
    }

    private sealed class ArenaPlayer() : Character(new UnitCustomModelParams())
    {
        public override int MaxHp => 100;
        public override int MaxMp => 100;

        public override void BroadcastPacket(GamePacket packet, bool self)
        {
            if (Region != null)
                base.BroadcastPacket(packet, self);
        }
    }

    private static void Set<T>(T target, string name, object value) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private void Install<T>(T manager) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _originals.Add((field, field.GetValue(null)));
        field.SetValue(null, manager);
    }
}
