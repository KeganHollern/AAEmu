using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;

using Moq;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class FamilyPersistenceTests : IAsyncLifetime
{
    private readonly SocialPersistenceSaveScope _saveScope = new();

    private const uint OwnerId = 4_101_510;
    private const uint MemberId = 4_101_511;
    private const uint OtherOwnerId = 4_101_512;
    private const uint OtherMemberId = 4_101_513;
    private const uint FamilyId = 4_101_514;
    private const uint OtherFamilyId = 4_101_515;

    public ValueTask InitializeAsync()
    {
        Cleanup();
        foreach (var id in new[] { OwnerId, MemberId, OtherOwnerId, OtherMemberId })
            Execute($"""
                INSERT INTO `characters`
                    (`id`,`account_id`,`name`,`race`,`gender`,`unit_model_params`,`level`,`experience`,
                     `recoverable_exp`,`hp`,`mp`,`consumed_lp`,`ability1`,`ability2`,`ability3`,
                     `world_id`,`zone_id`,`x`,`y`,`z`,`faction_id`,`faction_name`,`expedition_id`,
                     `family`,`dead_count`,`rez_wait_duration`,`rez_penalty_duration`,`money`,
                     `auto_use_aapoint`,`prev_point`,`point`,`gift`,`expanded_expert`,`slots`)
                VALUES ({id},{id},'Family{id}',1,1,X'',1,0,0,100,100,0,1,2,3,
                        1,1,0,0,0,1,'',0,0,0,0,0,1234,0,0,0,0,0,X'');
                """);
        FamilyManager.SaveFamily(NewFamily(FamilyId, OwnerId, MemberId));
        FamilyManager.SaveFamily(NewFamily(OtherFamilyId, OtherOwnerId, OtherMemberId));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Cleanup();
        }
        finally
        {
            _saveScope.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ChangeTitleAndOwner_RestartRetainsTitleAndOneSteward()
    {
        var manager = LoadManager();
        var owner = new Character(null) { Id = OwnerId, Family = FamilyId };
        manager.ChangeTitle(owner, MemberId, "Cousin");
        manager.ChangeOwner(owner, MemberId);

        var restarted = LoadManager();
        var family = restarted.GetFamily(FamilyId);
        Assert.Equal("Cousin", family.GetMember(MemberId).Title);
        Assert.Equal(1, family.GetMember(MemberId).Role);
        Assert.Equal(0, family.GetMember(OwnerId).Role);
        Assert.Single(family.Members.Where(member => member.Role == 1));
        Assert.Equal(FamilyId, restarted.GetFamilyOfCharacter(MemberId));
        Assert.Equal(1234, Scalar($"SELECT money FROM characters WHERE id={MemberId}"));
        Assert.Equal(1, Scalar($"SELECT role FROM family_members WHERE character_id={OtherOwnerId} AND family_id={OtherFamilyId}"));
    }

    [Theory]
    [InlineData(OtherMemberId)]
    [InlineData(4_101_599u)]
    public void FamilyActions_OutsideOrMissingMember_LeaveStoredFamiliesUnchanged(uint targetId)
    {
        var manager = LoadManager();
        var owner = new Character(null) { Id = OwnerId, Family = FamilyId };
        manager.ChangeTitle(owner, targetId, "Wrong family");
        manager.ChangeOwner(owner, targetId);
        manager.KickMember(owner, targetId);

        var restarted = LoadManager();
        Assert.Equal(2, restarted.GetFamily(FamilyId).Members.Count);
        Assert.Equal(2, restarted.GetFamily(OtherFamilyId).Members.Count);
        Assert.Equal("Member", restarted.GetFamily(OtherFamilyId).GetMember(OtherMemberId).Title);
        Assert.Equal(1, restarted.GetFamily(FamilyId).GetMember(OwnerId).Role);
        Assert.Equal(0, restarted.GetFamily(OtherFamilyId).GetMember(OtherMemberId).Role);
    }

    [Fact]
    public void ChangeOwner_WriteFails_RetainsThePreviousStewardInMemoryAndAfterRestart()
    {
        var manager = LoadManager();
        var owner = new Character(null) { Id = OwnerId, Family = FamilyId };
        Execute($"""
            CREATE TRIGGER `fail_family_member_write` BEFORE INSERT ON `family_members`
            FOR EACH ROW BEGIN
                IF NEW.character_id = {MemberId} AND NEW.role = 1 THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected family owner failure';
                END IF;
            END
            """);

        manager.ChangeOwner(owner, MemberId);

        Assert.Equal(1, manager.GetFamily(FamilyId).GetMember(OwnerId).Role);
        Assert.Equal(0, manager.GetFamily(FamilyId).GetMember(MemberId).Role);
        var restarted = LoadManager();
        Assert.Equal(1, restarted.GetFamily(FamilyId).GetMember(OwnerId).Role);
        Assert.Equal(0, restarted.GetFamily(FamilyId).GetMember(MemberId).Role);
        Execute("DROP TRIGGER `fail_family_member_write`");
        manager.ChangeOwner(owner, MemberId);
        Assert.Equal(1, LoadManager().GetFamily(FamilyId).GetMember(MemberId).Role);
    }

    [Fact]
    public void SaveFamily_CharacterMembershipAndMemberRowsCommitTogether()
    {
        Execute($"DELETE FROM family_members WHERE family_id={FamilyId}; UPDATE characters SET family=0 WHERE id IN ({OwnerId},{MemberId})");
        var family = NewFamily(FamilyId, OwnerId, MemberId);
        Execute($"""
            CREATE TRIGGER `fail_family_character_write` BEFORE UPDATE ON `characters`
            FOR EACH ROW BEGIN
                IF NEW.id = {MemberId} AND NEW.family = {FamilyId} THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Injected family membership failure';
                END IF;
            END
            """);

        Assert.ThrowsAny<Exception>(() => FamilyManager.SaveFamily(family));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM family_members WHERE family_id={FamilyId}"));
        Assert.Equal(0, Scalar($"SELECT SUM(family) FROM characters WHERE id IN ({OwnerId},{MemberId})"));
        Execute("DROP TRIGGER `fail_family_character_write`");
        FamilyManager.SaveFamily(family);
        Assert.Equal(FamilyId, Scalar($"SELECT family FROM characters WHERE id={OwnerId}"));
        Assert.Equal(FamilyId, Scalar($"SELECT family FROM characters WHERE id={MemberId}"));
        Assert.Equal(2, LoadManager().GetFamily(FamilyId).Members.Count);
    }

    [Fact]
    public void KickMember_OfflineMember_DisbandPersistsBeforeRestart()
    {
        var manager = LoadManager();
        manager.KickMember(new Character(null) { Id = OwnerId, Family = FamilyId }, MemberId);

        Assert.Null(manager.GetFamily(FamilyId));
        Assert.Null(LoadManager().GetFamily(FamilyId));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM family_members WHERE family_id={FamilyId}"));
        Assert.Equal(0, Scalar($"SELECT SUM(family) FROM characters WHERE id IN ({OwnerId},{MemberId})"));
        Assert.Equal(2, LoadManager().GetFamily(OtherFamilyId).Members.Count);
    }

    [Fact]
    public void SaveFamily_RemovalRollback_RetainsTheRemovalForRetry()
    {
        var family = LoadManager().GetFamily(FamilyId);
        family.RemoveMember(family.GetMember(MemberId));
        using (var connection = MySQL.CreateConnection())
        using (var transaction = connection.BeginTransaction())
        {
            family.Save(connection, transaction);
            transaction.Rollback();
        }
        Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM family_members WHERE family_id={FamilyId}"));

        FamilyManager.SaveFamily(family);

        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM family_members WHERE family_id={FamilyId}"));
        Assert.Equal(0, Scalar($"SELECT family FROM characters WHERE id={MemberId}"));
    }

    [Fact]
    public void RemoveDeletedCharacter_StewardOfThreeMembers_DisbandSurvivesRestart()
    {
        Execute($"DELETE FROM family_members WHERE family_id={OtherFamilyId}; UPDATE characters SET family=0 WHERE family={OtherFamilyId}");
        var family = NewFamily(FamilyId, OwnerId, MemberId);
        family.AddMember(new FamilyMember { Id = OtherMemberId, Name = $"Family{OtherMemberId}", Role = 0, Title = "Cousin" });
        FamilyManager.SaveFamily(family);
        var manager = LoadManager();
        var owner = new Character(null) { Id = OwnerId, Family = FamilyId };

        manager.LeaveFamily(owner);
        Assert.Equal(3, LoadManager().GetFamily(FamilyId).Members.Count);
        manager.RemoveDeletedCharacter(owner);

        Assert.Null(manager.GetFamily(FamilyId));
        Assert.Null(LoadManager().GetFamily(FamilyId));
        Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM family_members WHERE family_id={FamilyId}"));
        Assert.Equal(0, Scalar($"SELECT SUM(family) FROM characters WHERE id IN ({OwnerId},{MemberId},{OtherMemberId})"));
    }

    private static FamilyManager LoadManager()
    {
        var manager = new FamilyManager(Mock.Of<IWorldManager>(), Mock.Of<IChatManager>(), Mock.Of<IFamilyIdManager>());
        manager.Load();
        return manager;
    }

    private static Family NewFamily(uint familyId, uint ownerId, uint memberId)
    {
        var family = new Family { Id = familyId };
        family.AddMember(new FamilyMember { Id = ownerId, Name = $"Family{ownerId}", Role = 1, Title = "" });
        family.AddMember(new FamilyMember { Id = memberId, Name = $"Family{memberId}", Role = 0, Title = "Member" });
        return family;
    }

    private static void Cleanup() => Execute($"""
        DROP TRIGGER IF EXISTS `fail_family_member_write`;
        DROP TRIGGER IF EXISTS `fail_family_character_write`;
        DELETE FROM family_members WHERE family_id IN ({FamilyId},{OtherFamilyId});
        DELETE FROM characters WHERE id IN ({OwnerId},{MemberId},{OtherOwnerId},{OtherMemberId});
        """);

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
