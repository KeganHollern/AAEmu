using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.InstantGame;
using AAEmu.Game.Models.Game.InstantGame.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
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
                corps2_id INTEGER, time_opening INTEGER, time_ending INTEGER, time_playing INTEGER, victory_score INTEGER);
            INSERT INTO game_rule_sets VALUES(22,7,5,159,160,4,1,15,0),(28,11,1,159,160,2,1,10,60);
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
        Install(new WorldManager(null, null, null, null, null));
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
        AppConfiguration.Instance.World = _previousWorld;
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        foreach (var (field, original) in _originals)
            field.SetValue(null, original);
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
        await Assert.That((camp.Id, camp.EnemyPlayerKillScore, camp.VictoryScore, camp.TimePlaying)).IsEqualTo((22u, 3, 0, 15));
        await Assert.That((duel.Id, duel.EnemyPlayerKillScore, duel.VictoryScore, duel.TimePlaying)).IsEqualTo((28u, 30, 60, 10));
    }

    private void Start(uint id, bool finishCountdown = true)
    {
        var field = _fields.GetBattlefield(id);
        _game = new InstantGame(field, _world, _clock);
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

    private PacketStream[] Packets(ushort opcode) => _observer.Packets
        .Where(bytes => bytes.Length >= 8 && BitConverter.ToUInt16(bytes, 6) == opcode)
        .Select(bytes => new PacketStream(bytes) { Pos = 16 }).ToArray();

    private static (byte Corps, int Award, int Red, int Blue, string Killer) ReadPoints(PacketStream stream) =>
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

    private static async Task CheckTeam(PacketStream stream, int state, uint faction, int score, int kills, int deaths)
    {
        var actual = (stream.ReadByte(), stream.ReadUInt32(), stream.ReadInt32(), stream.ReadInt16(), stream.ReadInt16());
        await Assert.That(actual).IsEqualTo(((byte)state, faction, score, (short)kills, (short)deaths));
        var members = stream.ReadByte();
        var personalScores = 0;
        for (var i = 0; i < members; i++)
        {
            stream.ReadUInt32();
            stream.ReadString();
            personalScores += stream.ReadInt32();
            stream.ReadInt32();
            stream.ReadUInt32();
            stream.ReadUInt16();
            stream.ReadUInt16();
            stream.ReadUInt16();
            stream.ReadUInt16();
        }
        await Assert.That(personalScores).IsEqualTo(score);
    }

    private sealed class ArenaPlayer : CharacterMock
    {
        public override int MaxHp => 100;
        public override int MaxMp => 100;
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
