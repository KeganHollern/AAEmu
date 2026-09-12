using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.StaticValues;
using MySql.Data.MySqlClient;
using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class ExpeditionPersistenceTests : IDisposable
{
    private readonly SocialPersistenceSaveScope _saveScope = new();

    private const uint OwnerId = 4_120_001;
    private const uint MemberId = 4_120_002;
    private const FactionsEnum GuildId = (FactionsEnum)4_120_000;
    private readonly ExpeditionConfig _previousConfig;

    public ExpeditionPersistenceTests()
    {
        _previousConfig = AppConfiguration.Instance.Expedition;
        AppConfiguration.Instance.Expedition = new ExpeditionConfig { NameRegex = "^[a-zA-Zа-яА-Я ]{3,32}$" };
        Cleanup();
        SeedCharacter(OwnerId);
        SeedCharacter(MemberId);
    }

    public void Dispose()
    {
        try
        {
            Cleanup();
        }
        finally
        {
            AppConfiguration.Instance.Expedition = _previousConfig;
            _saveScope.Dispose();
        }
    }

    [Fact]
    public void SaveReload_RenameOwnerRolesAndAllFlags_RetainsCommittedState()
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        Assert.Equal((uint)GuildId, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
        guild.Name = "Renamed Guild";
        guild.OwnerId = MemberId;
        guild.OwnerName = "New Owner";
        guild.Members[0].Role = 0;
        guild.Members[1].Role = 255;
        foreach (var policy in guild.Policies)
        {
            policy.DominionDeclare = true;
            policy.Invite = true;
            policy.Expel = true;
            policy.Promote = true;
            policy.Dismiss = true;
            policy.Chat = true;
            policy.ManagerChat = true;
            policy.SiegeMaster = true;
            policy.JoinSiege = true;
        }
        ExpeditionManager.Save(guild);

        var reloaded = Reload();
        Assert.Equal("Renamed Guild", reloaded.Name);
        Assert.Equal(MemberId, reloaded.OwnerId);
        Assert.Equal("New Owner", reloaded.OwnerName);
        Assert.Equal(0, reloaded.GetMember(OwnerId).Role);
        Assert.Equal(255, reloaded.GetMember(MemberId).Role);
        foreach (var policy in reloaded.Policies)
            Assert.True(policy.DominionDeclare && policy.Invite && policy.Expel && policy.Promote &&
                policy.Dismiss && policy.Chat && policy.ManagerChat && policy.SiegeMaster && policy.JoinSiege);
    }

    [Fact]
    public void Save_Disband_ClearsEveryMembershipAndPolicyBeforeRestart()
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        guild.isDisbanded = true;
        ExpeditionManager.Save(guild);
        Assert.Null(Reload());
        Assert.Equal(0u, Scalar($"SELECT COUNT(*) FROM expedition_members WHERE expedition_id = {(uint)GuildId}"));
        Assert.Equal(0u, Scalar($"SELECT COUNT(*) FROM expedition_role_policies WHERE expedition_id = {(uint)GuildId}"));
        Assert.Equal(0u, Scalar($"SELECT expedition_id FROM characters WHERE id = {OwnerId}"));
        Assert.Equal(0u, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
    }

    [Fact]
    public void Save_RemovedMember_DeletesMembershipAndCharacterLinkTogether()
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        guild.RemoveMember(guild.GetMember(MemberId));
        ExpeditionManager.Save(guild);
        Assert.Null(Reload().GetMember(MemberId));
        Assert.Equal(0u, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
        Assert.Equal((uint)GuildId, Scalar($"SELECT expedition_id FROM characters WHERE id = {OwnerId}"));
    }

    [Fact]
    public void Save_DisbandFailure_RollsBackDeletedMembershipAndCharacterLink()
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        Execute($"CREATE TRIGGER expedition_disband_failure BEFORE DELETE ON expeditions FOR EACH ROW BEGIN IF OLD.id = {(uint)GuildId} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Guild test failure'; END IF; END");
        try
        {
            guild.isDisbanded = true;
            Assert.Throws<MySqlException>(() => ExpeditionManager.Save(guild));
            Assert.NotNull(Reload());
            Assert.Equal(2u, Scalar($"SELECT COUNT(*) FROM expedition_members WHERE expedition_id = {(uint)GuildId}"));
            Assert.Equal((uint)GuildId, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
        }
        finally
        {
            Execute("DROP TRIGGER IF EXISTS expedition_disband_failure");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_RemovalFailure_RollsBackDeletedMemberAndCharacterLink(bool lateFailure)
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        Execute(lateFailure
            ? $"CREATE TRIGGER expedition_member_failure BEFORE INSERT ON expedition_role_policies FOR EACH ROW BEGIN IF NEW.expedition_id = {(uint)GuildId} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Guild test failure'; END IF; END"
            : $"CREATE TRIGGER expedition_member_failure BEFORE UPDATE ON characters FOR EACH ROW BEGIN IF OLD.id = {MemberId} AND NEW.expedition_id = 0 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Guild test failure'; END IF; END");
        try
        {
            guild.RemoveMember(guild.GetMember(MemberId));
            Assert.Throws<MySqlException>(() => ExpeditionManager.Save(guild));
            Assert.NotNull(Reload().GetMember(MemberId));
            Assert.Equal((uint)GuildId, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
        }
        finally
        {
            Execute("DROP TRIGGER IF EXISTS expedition_member_failure");
        }
        ExpeditionManager.Save(guild);
        Assert.Null(Reload().GetMember(MemberId));
        Assert.Equal(0u, Scalar($"SELECT expedition_id FROM characters WHERE id = {MemberId}"));
    }

    [Theory]
    [InlineData("guild", false)]
    [InlineData("guild", true)]
    [InlineData("family", false)]
    [InlineData("family", true)]
    [InlineData("all_families", false)]
    [InlineData("all_families", true)]
    public void Save_AcknowledgementFails_StopsLaterGuildAndFamilyPersistence(string kind, bool committed)
    {
        var guild = CreateGuild();
        ExpeditionManager.Save(guild);
        var family = new Family { Id = (uint)GuildId };
        family.AddMember(new FamilyMember { Id = OwnerId, Name = "Owner", Role = 1, Title = "" });
        family.AddMember(new FamilyMember { Id = MemberId, Name = "Member", Role = 0, Title = "Member" });
        FamilyManager.SaveFamily(family);
        var families = new FamilyManager(Mock.Of<IWorldManager>(), Mock.Of<IChatManager>(), Mock.Of<IFamilyIdManager>());
        families.Load();
        family = families.GetFamily((uint)GuildId);
        guild.Name = "Changed Guild";
        family.GetMember(MemberId).Title = "Changed";
        var commitCalls = 0;
        var stopCalls = 0;
        _saveScope.Save.StopForConsistencyFailure = (_, _) => stopCalls++;
        _saveScope.Save.CommitTransaction = transaction =>
        {
            commitCalls++;
            if (committed)
                transaction.Commit();
            else
                transaction.Rollback();
            throw new IOException("Injected acknowledgement failure");
        };
        Action write = kind switch
        {
            "guild" => () => ExpeditionManager.Save(guild),
            "family" => () => FamilyManager.SaveFamily(family),
            _ => families.SaveAllFamilies
        };

        Assert.Throws<IOException>(write);
        Assert.Equal(1, stopCalls);
        Assert.Equal(committed && kind == "guild" ? "Changed Guild" : "Guild", Reload().Name);
        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT title FROM family_members WHERE character_id = {MemberId}";
            Assert.Equal(committed && kind != "guild" ? "Changed" : "Member", command.ExecuteScalar());
        }

        // The test stop hook returns. Every later social helper must still stop before SQL.
        Assert.Throws<InvalidOperationException>(() => ExpeditionManager.Save(guild));
        Assert.Throws<InvalidOperationException>(() => FamilyManager.SaveFamily(family));
        Assert.Throws<InvalidOperationException>(families.SaveAllFamilies);
        Assert.Equal(1, commitCalls);
        Assert.Equal(1, stopCalls);
    }

    private static Expedition CreateGuild() => new()
    {
        Id = GuildId, MotherId = FactionsEnum.Nuian, Name = "Guild", OwnerId = OwnerId, OwnerName = "Owner",
        Created = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc),
        Members = [Member(OwnerId, 255), Member(MemberId, 0)],
        Policies = [new ExpeditionRolePolicy { ExpeditionId = GuildId, Role = 255, Name = "Owner" },
            new ExpeditionRolePolicy { ExpeditionId = GuildId, Role = 0, Name = "Member" }]
    };

    private static ExpeditionMember Member(uint id, byte role) => new()
    {
        ExpeditionId = GuildId, CharacterId = id, Role = role, Name = "Member", Memo = "", Level = 30,
        LastWorldLeaveTime = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)
    };

    private static Expedition Reload()
    {
        var manager = new ExpeditionManager(Mock.Of<IExpeditionIdManager>(), Mock.Of<ITeamManager>(),
            Mock.Of<IWorldManager>(), Mock.Of<IChatManager>());
        manager.Load();
        return manager.GetExpedition(GuildId);
    }

    private static void SeedCharacter(uint id)
    {
        // Populate required character columns without constructing unrelated gameplay managers.
        using var connection = MySQL.CreateConnection();
        var columns = new List<(string Name, string Type)>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SHOW COLUMNS FROM characters";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
                if (reader.GetString("Null") == "NO" && reader.IsDBNull(reader.GetOrdinal("Default")))
                    columns.Add((reader.GetString("Field"), reader.GetString("Type")));
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO characters ({string.Join(',', columns.Select(column => $"`{column.Name}`"))}) VALUES ({string.Join(',', columns.Select((_, index) => $"@v{index}"))})";
        for (var index = 0; index < columns.Count; index++)
        {
            var (name, type) = columns[index];
            object value = name == "id" || name == "account_id" ? id :
                type.Contains("blob", StringComparison.Ordinal) ? Array.Empty<byte>() :
                type.Contains("char", StringComparison.Ordinal) || type.Contains("text", StringComparison.Ordinal) ? "Test" :
                type.Contains("date", StringComparison.Ordinal) ? new DateTime(2026, 9, 12) : 0;
            command.Parameters.AddWithValue($"@v{index}", value);
        }
        command.ExecuteNonQuery();
    }

    private static uint Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToUInt32(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Cleanup() => Execute($"""
        DROP TRIGGER IF EXISTS expedition_disband_failure;
        DROP TRIGGER IF EXISTS expedition_member_failure;
        DELETE FROM family_members WHERE family_id = {(uint)GuildId};
        DELETE FROM expedition_members WHERE expedition_id = {(uint)GuildId};
        DELETE FROM expedition_role_policies WHERE expedition_id = {(uint)GuildId};
        DELETE FROM expeditions WHERE id = {(uint)GuildId};
        DELETE FROM characters WHERE id IN ({OwnerId}, {MemberId});
        """);
}
