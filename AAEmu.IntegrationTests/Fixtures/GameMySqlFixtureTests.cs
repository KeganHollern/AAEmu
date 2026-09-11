using Xunit;

namespace AAEmu.IntegrationTests.Fixtures;

[Trait("Category", "GameMySql")]
public sealed class GameMySqlFixtureTests
{
    [Theory]
    [InlineData("Server=127.0.0.1;Port=33306;User ID=test;Password=test")]
    [InlineData("Server=::1;Port=33306;User ID=test;Password=test")]
    public void LocalOverride_AcceptsOnlyLoopbackWithoutDatabase(string value)
    {
        var parsed = GameMySqlFixture.ParseLocalConnection(value);
        Assert.Equal(string.Empty, parsed.Database);
        Assert.Equal(33306u, parsed.Port);
    }

    [Theory]
    [InlineData("Server=db.example.com;User ID=test;Password=do-not-log")]
    [InlineData("Server=192.0.2.1;User ID=test;Password=do-not-log")]
    [InlineData("Server=127.0.0.1;Database=aaemu_game;Password=do-not-log")]
    [InlineData("Server=127.0.0.1;Database=any_test_name;Password=do-not-log")]
    [InlineData("Server=/tmp/mysql.sock;Protocol=unix;Password=do-not-log")]
    [InlineData("invalid-connection=do-not-log")]
    public void LocalOverride_RejectsExternalOrNamedTargetsWithoutLeakingValues(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => GameMySqlFixture.ParseLocalConnection(value));
        Assert.DoesNotContain("do-not-log", error.ToString());
    }
}
