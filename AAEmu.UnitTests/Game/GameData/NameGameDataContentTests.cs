using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Names;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public sealed class NameGameDataContentTests
{
    [Test]
    public async Task Load_R208022Compact_ProvidesAuthoredNameRules()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var data = new NameGameData();

        data.Load(connection);

        await Assert.That(data.Data.AllowedCharacters.Count).IsEqualTo(2352);
        await Assert.That(data.Data.BlockedNames.Length).IsEqualTo(15);
        await Assert.That(NameRules.IsReserved("ADMIN", data)).IsTrue();
        await Assert.That(NameRules.IsReserved("GM", data)).IsTrue();
        await Assert.That(NameRules.IsReserved("Gmster", data)).IsFalse();
        await Assert.That(NameRules.Validate("Éva", NameType.Character, "en_us", data)).IsEqualTo(NameValidationResult.Valid);
        await Assert.That(NameRules.Validate("Éva", NameType.Faction, "en_us", data)).IsEqualTo(NameValidationResult.Characters);
    }
}
