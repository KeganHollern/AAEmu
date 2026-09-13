using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed class CharacterCreationSlotPersistenceTests : IAsyncLifetime
{
    private const uint AccountId = 4_102_436;
    private const uint OtherAccountId = 4_102_437;
    private const uint FirstCharacterId = 4_102_440;

    public ValueTask InitializeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Creation_SeventhCharacterFailsBeforeStarterItemCallback()
    {
        for (var i = 0u; i < 6; i++)
            Assert.Equal(CharacterCreateError.Ok, CharacterCreationSlots.Create(AccountId, () => Insert(FirstCharacterId + i)));

        var called = false;
        var result = CharacterCreationSlots.Create(AccountId, () =>
        {
            called = true;
            return Insert(FirstCharacterId + 6);
        });
        Assert.Equal(CharacterCreateError.WorldCharacterLimit, result);
        Assert.False(called);
        Assert.Equal(6L, Count(AccountId));
    }

    [Fact]
    public void Creation_PendingDeletionStillUsesSlotAndCompletedDeletionFreesIt()
    {
        for (var i = 0u; i < 6; i++)
            Insert(FirstCharacterId + i);
        Execute($"UPDATE characters SET delete_request_time=UTC_TIMESTAMP(),delete_time=UTC_TIMESTAMP() - INTERVAL 1 SECOND WHERE id={FirstCharacterId}");
        Assert.Equal(CharacterCreateError.WorldCharacterLimit,
            CharacterCreationSlots.Create(AccountId, () => Insert(FirstCharacterId + 6)));

        Execute($"UPDATE characters SET deleted=1 WHERE id={FirstCharacterId}");
        Assert.Equal(CharacterCreateError.Ok,
            CharacterCreationSlots.Create(AccountId, () => Insert(FirstCharacterId + 6)));
        Assert.Equal(6L, Count(AccountId));
    }

    [Fact]
    public async Task Creation_ConcurrentRequestsForFinalSlotAdmitExactlyOne()
    {
        for (var i = 0u; i < 5; i++)
            Insert(FirstCharacterId + i);
        using var start = new ManualResetEventSlim();
        var first = Task.Run(() => Create(FirstCharacterId + 5), TestContext.Current.CancellationToken);
        var second = Task.Run(() => Create(FirstCharacterId + 6), TestContext.Current.CancellationToken);
        start.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, value => value == CharacterCreateError.Ok);
        Assert.Single(results, value => value == CharacterCreateError.WorldCharacterLimit);
        Assert.Equal(6L, Count(AccountId));
        return;

        CharacterCreateError Create(uint id)
        {
            start.Wait(TestContext.Current.CancellationToken);
            return CharacterCreationSlots.Create(AccountId, () => Insert(id));
        }
    }

    [Fact]
    public async Task Creation_DifferentAccountDoesNotWaitForHeldAccount()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = Task.Run(() => CharacterCreationSlots.Create(AccountId, () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return Insert(FirstCharacterId);
        }), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var other = await Task.Run(() => CharacterCreationSlots.Create(OtherAccountId,
                () => Insert(FirstCharacterId + 1, OtherAccountId)), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(CharacterCreateError.Ok, other);
        }
        finally
        {
            release.Set();
        }
        Assert.Equal(CharacterCreateError.Ok, await held);
    }

    [Fact]
    public void Creation_FailedSaveOrExceptionReleasesLockForNextRequest()
    {
        Assert.Equal(CharacterCreateError.Failed, CharacterCreationSlots.Create(AccountId, () => CharacterCreateError.Failed));
        Assert.Throws<InvalidOperationException>(() => CharacterCreationSlots.Create(AccountId,
            () => throw new InvalidOperationException("Injected creation save failure")));
        Assert.Equal(CharacterCreateError.Ok, CharacterCreationSlots.Create(AccountId, () => Insert(FirstCharacterId)));
        Assert.Equal(1L, Count(AccountId));
    }

    private static CharacterCreateError Insert(uint id, uint account = AccountId)
    {
        Execute($"""
            INSERT INTO characters
                (id,account_id,name,race,gender,unit_model_params,level,experience,recoverable_exp,hp,mp,
                 consumed_lp,ability1,ability2,ability3,world_id,zone_id,x,y,z,faction_id,faction_name,
                 expedition_id,family,dead_count,rez_wait_duration,rez_penalty_duration,money,
                 auto_use_aapoint,prev_point,point,gift,expanded_expert,slots)
            VALUES({id},{account},'Creation{id}',1,1,X'',1,0,0,100,100,0,1,11,11,1,1,0,0,0,1,'',
                   0,0,0,0,0,0,0,0,0,0,0,X'');
            """);
        return CharacterCreateError.Ok;
    }

    private static long Count(uint accountId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id=@account AND deleted=0";
        command.Parameters.AddWithValue("@account", accountId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string sql)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Cleanup()
    {
        Execute($"DELETE FROM characters WHERE account_id IN ({AccountId},{OtherAccountId})");
    }
}
