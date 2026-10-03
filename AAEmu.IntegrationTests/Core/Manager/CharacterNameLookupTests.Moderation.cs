using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;

using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class CharacterNameLookupTests
{
    [Theory]
    [InlineData("EVA", PlainId, "Eva")]
    [InlineData("ÉVA", AccentId, "Éva")]
    [InlineData("9438001", PlainId, "Eva")]
    [InlineData("9438002", AccentId, "Éva")]
    public void ModerationTarget_ExactRegisteredNameOrId_SelectsTheCorrectAccount(string input, uint id, string name)
    {
        var manager = new ModerationManager(null);

        Assert.True(manager.TryResolveTarget(input, out var target));

        Assert.Equal(id, target.CharacterId);
        Assert.Equal(id, target.AccountId);
        Assert.Equal(name, target.Name);
    }

    [Theory]
    [InlineData("Evà")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void ModerationTarget_UnknownOrEmptyName_DoesNotSelectAnAccount(string input)
    {
        var manager = new ModerationManager(null);

        Assert.False(manager.TryResolveTarget(input, out var target));
        Assert.Null(target);
    }

    [Fact]
    public void ModerationTarget_DeletedAccentedCharacter_DoesNotFallBackToPlainName()
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE characters SET deleted = 1 WHERE id = @id";
        command.Parameters.AddWithValue("@id", AccentId);
        command.ExecuteNonQuery();
        var manager = new ModerationManager(null);

        Assert.False(manager.TryResolveTarget("Éva", out var deleted));
        Assert.Null(deleted);
        Assert.True(manager.TryResolveTarget("Eva", out var plain));
        Assert.Equal(PlainId, plain.AccountId);
        Assert.Equal(PlainId, plain.CharacterId);
    }

    [Fact]
    public void ModerationTarget_ExplicitAccount_KeepsTheAccountSyntax()
    {
        var manager = new ModerationManager(null);

        Assert.True(manager.TryResolveTarget("account:9438002", out var target));

        Assert.Equal(AccentId, target.AccountId);
        Assert.Equal(0u, target.CharacterId);
    }
}
