using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

[Collection("GameMySql")]
[Trait("Category", "GameMySql")]
public sealed partial class CharacterNameLookupTests : IDisposable
{
    private const uint PlainId = 9_438_001;
    private const uint AccentId = 9_438_002;
    private static readonly FieldInfo s_names = typeof(Singleton<NameManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo s_world = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object _previousNames = s_names.GetValue(null);
    private readonly object _previousWorld = s_world.GetValue(null);
    private readonly NameManager _names = new();

    public CharacterNameLookupTests()
    {
        Cleanup();
        Seed(PlainId, "Eva");
        Seed(AccentId, "Éva");
        _names.Load([], [], []);
        _names.AddCharacter(PlainId, "Eva", PlainId);
        _names.AddCharacter(AccentId, "Éva", AccentId);
        s_names.SetValue(null, _names);
        s_world.SetValue(null, new WorldManager(null, null, null, null, null));
    }

    [Theory]
    [InlineData("EVA", PlainId, "Eva")]
    [InlineData("ÉVA", AccentId, "Éva")]
    public void OfflineFriend_AccentInsensitiveDatabase_ReturnsTheExactRegisteredName(string input, uint id, string name)
    {
        // This fixture reproduces the old ambiguous name query under utf8_general_ci.
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM characters WHERE id IN (@plain, @accent) AND name = 'Eva'";
        command.Parameters.AddWithValue("@plain", PlainId);
        command.Parameters.AddWithValue("@accent", AccentId);
        Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));

        var friend = FriendMananger.GetFriendInfo(input);

        Assert.NotNull(friend);
        Assert.Equal(id, friend.CharacterId);
        Assert.Equal(name, friend.Name);
        Assert.False(friend.IsOnline);
    }

    [Fact]
    public void PendingDeletion_AccentedName_DoesNotChangeThePlainNamesStatus()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE characters SET delete_request_time = '2026-10-03 00:00:00' WHERE id = @id";
        command.Parameters.AddWithValue("@id", AccentId);
        command.ExecuteNonQuery();
        var manager = new CharacterManager(null, null, _names, null, null, null, null, null, null, null, null);

        Assert.False(manager.IsCharacterPendingDeletion("EVA"));
        Assert.True(manager.IsCharacterPendingDeletion("ÉVA"));
        Assert.False(manager.IsCharacterPendingDeletion("Evà"));
        Assert.Null(FriendMananger.GetFriendInfo("Evà"));
    }

    public void Dispose()
    {
        try
        {
            Cleanup();
        }
        finally
        {
            s_names.SetValue(null, _previousNames);
            s_world.SetValue(null, _previousWorld);
        }
    }

    private static void Seed(uint id, string name)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO characters
                (id,account_id,name,race,gender,unit_model_params,level,experience,
                 recoverable_exp,hp,mp,consumed_lp,ability1,ability2,ability3,
                 world_id,zone_id,x,y,z,faction_id,faction_name,expedition_id,
                 family,dead_count,rez_wait_duration,rez_penalty_duration,money,
                 auto_use_aapoint,prev_point,point,gift,expanded_expert,slots)
            VALUES (@id,@id,@name,1,1,X'',1,0,0,100,100,0,1,2,3,
                    1,1,0,0,0,1,'',0,0,0,0,0,0,0,0,0,0,0,X'')
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.ExecuteNonQuery();
    }

    private static void Cleanup()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM characters WHERE id IN (@plain, @accent)";
        command.Parameters.AddWithValue("@plain", PlainId);
        command.Parameters.AddWithValue("@accent", AccentId);
        command.ExecuteNonQuery();
    }
}
