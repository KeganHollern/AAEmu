using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class CharacterNameLookupTests
{
    private const uint FriendOwnerId = 9_438_003;
    private const uint FirstFriendRowId = 9_438_011;
    private const uint SecondFriendRowId = 9_438_012;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveFriend_DeletedCharacterOrReusedName_RemovesTheSavedIdentity(bool reusedName)
    {
        MarkAccentDeleted(reusedName);
        using var scope = new FriendLookupScope();
        var book = scope.LoadBook(AccentId);

        book.RemoveFriend("ÉVA");
        scope.SaveBook(book);

        Assert.Empty(book.FriendsIdList);
        Assert.Empty(scope.LoadBook().FriendsIdList);
        Assert.Equal(reusedName ? PlainId : 0u, _names.GetCharacterId("Éva"));
    }

    [Fact]
    public void RemoveFriend_AccentName_RemovesOnlyTheExactSavedName()
    {
        using var scope = new FriendLookupScope();
        var book = scope.LoadBook(PlainId, AccentId);

        book.RemoveFriend("ÉVA");
        scope.SaveBook(book);

        Assert.Single(book.FriendsIdList);
        Assert.True(book.FriendsIdList.ContainsKey(PlainId));
        var restarted = scope.LoadBook();
        Assert.Single(restarted.FriendsIdList);
        Assert.True(restarted.FriendsIdList.ContainsKey(PlainId));
    }

    [Fact]
    public void RemoveFriend_DeletedAndRecreatedNames_RemovesOneSavedIdentityPerRequest()
    {
        MarkAccentDeleted(true);
        using var scope = new FriendLookupScope();
        var book = scope.LoadBook(AccentId, PlainId);

        book.RemoveFriend("Éva");
        scope.SaveBook(book);

        Assert.Single(book.FriendsIdList);
        Assert.True(book.FriendsIdList.ContainsKey(PlainId));
        book = scope.LoadBook();
        Assert.Single(book.FriendsIdList);
        Assert.True(book.FriendsIdList.ContainsKey(PlainId));

        book.RemoveFriend("Éva");
        scope.SaveBook(book);

        Assert.Empty(book.FriendsIdList);
        Assert.Empty(scope.LoadBook().FriendsIdList);
    }

    private void MarkAccentDeleted(bool reusedName)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE characters SET deleted = 1 WHERE id = @accent";
        command.Parameters.AddWithValue("@accent", AccentId);
        command.ExecuteNonQuery();
        _names.RemoveCharacterId(AccentId);
        if (!reusedName)
            return;
        command.CommandText = "UPDATE characters SET name = @name WHERE id = @plain";
        command.Parameters.AddWithValue("@name", "Éva");
        command.Parameters.AddWithValue("@plain", PlainId);
        command.ExecuteNonQuery();
        _names.RemoveCharacterId(PlainId);
        _names.AddCharacter(PlainId, "Éva", PlainId);
    }

    private sealed class FriendLookupScope : IDisposable
    {
        private static readonly FieldInfo s_friends = typeof(Singleton<FriendMananger>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previousFriends = s_friends.GetValue(null);
        private readonly FriendMananger _friends = new();
        private readonly Character _owner = new(null) { Id = FriendOwnerId };

        public FriendLookupScope()
        {
            DeleteRows();
            s_friends.SetValue(null, _friends);
        }

        public CharacterFriends LoadBook(params uint[] friendIds)
        {
            using var connection = MySQL.CreateConnection();
            for (var index = 0; index < friendIds.Length; index++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO friends (id, friend_id, owner) VALUES (@id, @friend, @owner)";
                command.Parameters.AddWithValue("@id", FirstFriendRowId + (uint)index);
                command.Parameters.AddWithValue("@friend", friendIds[index]);
                command.Parameters.AddWithValue("@owner", FriendOwnerId);
                command.ExecuteNonQuery();
            }

            _friends.Load();
            var book = new CharacterFriends(_owner);
            book.Load(connection);
            return book;
        }

        public void SaveBook(CharacterFriends book)
        {
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            book.Save(connection, transaction);
            transaction.Commit();
        }

        public void Dispose()
        {
            try
            {
                DeleteRows();
            }
            finally
            {
                s_friends.SetValue(null, _previousFriends);
            }
        }

        private static void DeleteRows()
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM friends WHERE owner = @owner AND id IN (@first, @second)";
            command.Parameters.AddWithValue("@owner", FriendOwnerId);
            command.Parameters.AddWithValue("@first", FirstFriendRowId);
            command.Parameters.AddWithValue("@second", SecondFriendRowId);
            command.ExecuteNonQuery();
        }
    }
}
