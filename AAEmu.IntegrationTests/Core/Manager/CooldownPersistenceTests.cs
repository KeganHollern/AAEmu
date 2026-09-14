using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.IntegrationTests.Fixtures;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class CooldownPersistenceTests(GameMySqlFixture fixture)
{
    private readonly GameMySqlFixture _fixture = fixture;
    private const uint CharacterId = 944449;
    private const string Migration = "2026-09-13_aaemu_game_character_cooldown_tags.sql";

    [Fact]
    public void SaveLoad_BothBucketsRetainDurationExpiryAndExactWireValues()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        var cooldowns = new UnitCooldowns(clock);
        cooldowns.AddCooldown(50, 60000);
        cooldowns.AddCooldown(11715, 90000, 30);
        Save(cooldowns);
        clock.Advance(20000);
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.True(restored.CheckCooldown(50));
        Assert.True(restored.CheckCooldown(new SkillTemplate { Id = 11718, CooldownTagId = 30 }));
        var stream = new SCCooldownsPacket(restored).Write(new PacketStream());
        stream.Rollback();
        Assert.Equal(1u, stream.ReadUInt32());
        Assert.Equal(50u, stream.ReadUInt32());
        Assert.Equal(60000u, stream.ReadUInt32());
        Assert.Equal(40000u, stream.ReadUInt32());
        Assert.Equal(1u, stream.ReadUInt32());
        Assert.Equal(30u, stream.ReadUInt32());
        Assert.Equal(90000u, stream.ReadUInt32());
        Assert.Equal(70000u, stream.ReadUInt32());
        Assert.Equal(0, stream.LeftBytes);
        restored.RemoveTagCooldown(30);
        Save(restored);
        var nextLogin = new UnitCooldowns(clock);
        nextLogin.Load(CharacterId);
        Assert.False(nextLogin.CheckTagCooldown(30));
        Assert.True(nextLogin.CheckCooldown(50));
    }

    [Fact]
    public void LegacySkillRows_WithTheSameTagRestoreTheLongestSharedTimer()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        Save(new UnitCooldowns(clock));
        var first = clock.GetUtcNow().UtcDateTime.AddMilliseconds(30123);
        var second = clock.GetUtcNow().UtcDateTime.AddMilliseconds(60567);
        Insert("character_cooldowns", "skill_id", 11715, 90000, first);
        Insert("character_cooldowns", "skill_id", 11718, 0, second);
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.Empty(restored.GetActiveBuckets(150).Skills);
        Assert.Equal(new UnitCooldowns.CooldownSnapshot(30, 60567, 60567), restored.GetActiveBuckets(150).Tags.Single());
        Assert.True(restored.CheckCooldown(new SkillTemplate { Id = 11722, CooldownTagId = 30 }));
        Save(restored);
        Assert.Equal(0L, Count("character_cooldowns"));
        Assert.Equal(1L, Count("character_cooldown_tags"));
        var nextLogin = new UnitCooldowns(clock);
        nextLogin.Load(CharacterId);
        Assert.Equal(restored.GetActiveBuckets(150).Tags, nextLogin.GetActiveBuckets(150).Tags);
    }

    [Fact]
    public void LegacyAndSharedRow_WithTheSameExpiryKeepTheFullActualDuration()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        Save(new UnitCooldowns(clock));
        var expiry = clock.GetUtcNow().UtcDateTime.AddMilliseconds(30123);
        Insert("character_cooldowns", "skill_id", 11715, 0, expiry);
        Insert("character_cooldown_tags", "tag_id", 30, 90000, expiry);
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.Equal(new UnitCooldowns.CooldownSnapshot(30, 90000, 30123), restored.GetActiveBuckets(150).Tags.Single());
    }

    [Fact]
    public void ExpiredRows_AreRemovedFromBothBuckets()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        Save(new UnitCooldowns(clock));
        var expired = clock.GetUtcNow().UtcDateTime.AddMilliseconds(-1);
        Insert("character_cooldowns", "skill_id", 50, 30000, expired);
        Insert("character_cooldown_tags", "tag_id", 30, 30000, expired);
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.Empty(restored.GetActiveBuckets(150).Skills);
        Assert.Empty(restored.GetActiveBuckets(150).Tags);
        Assert.Equal(0L, Count("character_cooldowns"));
        Assert.Equal(0L, Count("character_cooldown_tags"));
    }

    [Fact]
    public void CharacterTransactionRollback_PreservesBothSavedBuckets()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        var before = new UnitCooldowns(clock);
        before.AddCooldown(50, 60000);
        before.AddCooldown(11715, 90000, 30);
        Save(before);
        var after = new UnitCooldowns(clock);
        after.AddCooldown(60, 120000);
        after.AddCooldown(88, 180000, 144);
        using (var connection = MySQL.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            after.Save(connection, transaction, CharacterId);
            transaction.Rollback();
        }
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.Equal(before.GetActiveBuckets(150).Skills, restored.GetActiveBuckets(150).Skills);
        Assert.Equal(before.GetActiveBuckets(150).Tags, restored.GetActiveBuckets(150).Tags);
    }

    [Fact]
    public void SharedBucketFailure_RollsBackTheIndividualBucketWithinTheCharacterTransaction()
    {
        using var skills = new SkillScope();
        var clock = new Clock();
        var before = new UnitCooldowns(clock);
        before.AddCooldown(50, 60000);
        before.AddCooldown(11715, 90000, 30);
        Save(before);
        var after = new UnitCooldowns(clock);
        after.AddCooldown(60, 120000);
        after.AddCooldown(88, 180000, 144);
        Execute("CREATE TRIGGER combat_cooldown_reject BEFORE INSERT ON character_cooldown_tags FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected cooldown write failure'");
        try
        {
            // Save catches the bucket failure. The outer character transaction still commits.
            Save(after);
        }
        finally
        {
            Execute("DROP TRIGGER combat_cooldown_reject");
        }
        var restored = new UnitCooldowns(clock);
        restored.Load(CharacterId);
        Assert.Equal(before.GetActiveBuckets(150).Skills, restored.GetActiveBuckets(150).Skills);
        Assert.Equal(before.GetActiveBuckets(150).Tags, restored.GetActiveBuckets(150).Tags);
    }

    [Fact]
    public void Migration_IsIdempotentAndMatchesTheBaseTable()
    {
        var before = TableDefinition();
        // All operations use the fixture's random disposable schema.
        Execute("DROP TABLE character_cooldown_tags");
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SQL", "updates", Migration));
        try
        {
            Execute(migration);
            Execute(migration);
            Assert.Equal(before, TableDefinition());
        }
        finally
        {
            Execute(migration);
        }
    }

    private static void Save(UnitCooldowns cooldowns)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        cooldowns.Save(connection, transaction, CharacterId);
        transaction.Commit();
    }

    private static void Insert(string table, string column, uint id, uint duration, DateTime expiry)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO `{table}` (character_id, `{column}`, duration_ms, expires_at) VALUES (@character, @id, @duration, @expiry)";
        command.Parameters.AddWithValue("@character", CharacterId);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@duration", duration);
        command.Parameters.AddWithValue("@expiry", expiry);
        command.ExecuteNonQuery();
    }

    private static long Count(string table)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM `{table}` WHERE character_id=@character";
        command.Parameters.AddWithValue("@character", CharacterId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string TableDefinition()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SHOW CREATE TABLE character_cooldown_tags";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return reader.GetString(1);
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int milliseconds) => _now = _now.AddMilliseconds(milliseconds);
    }

    private sealed class SkillScope : IDisposable
    {
        private readonly FieldInfo _field = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previous;
        public SkillScope()
        {
            _previous = _field.GetValue(null);
            var manager = new SkillManager(null, null);
            typeof(SkillManager).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager,
                new Dictionary<uint, SkillTemplate>
                {
                    [50] = new() { Id = 50 },
                    [11715] = new() { Id = 11715, CooldownTagId = 30 },
                    [11718] = new() { Id = 11718, CooldownTagId = 30 }
                });
            _field.SetValue(null, manager);
        }
        public void Dispose() => _field.SetValue(null, _previous);
    }
}
