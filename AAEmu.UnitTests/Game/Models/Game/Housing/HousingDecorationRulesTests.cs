using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class HousingDecorationRulesTests
{
    [Test]
    public async Task Check_IncompleteHouse_RejectsBeforeCapacity()
    {
        await Assert.That(Check([], step: 0, allCount: 200)).IsEqualTo(ErrorMessageType.HouseNotDecoratableState);
    }

    [Test]
    public async Task Check_AbsoluteLimitIncludesBoundDoodads()
    {
        await Assert.That(Check([], allCount: 49)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        await Assert.That(Check([], allCount: 50)).IsEqualTo(ErrorMessageType.HouseTooManyDecorations);
    }

    [Test]
    public async Task Check_RestorableLimitDoesNotLimitDisposableFurniture()
    {
        var placed = Enumerable.Range(1, 40).Select(id => Placed((uint)id, restore: true)).ToArray();
        await Assert.That(Check(placed, restore: true)).IsEqualTo(ErrorMessageType.NoMoreSpaceToDecorate);
        await Assert.That(Check(placed, restore: false)).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Check_SpecialtyCategoryCountsDistinctItemTypes()
    {
        var duplicates = new[] { Placed(1, 1), Placed(1, 1), Placed(1, 1) };
        await Assert.That(Check(duplicates, group: 1)).IsEqualTo(ErrorMessageType.NoErrorMessage);
        var full = new[] { Placed(1, 1), Placed(2, 1), Placed(3, 1) };
        await Assert.That(Check(full, group: 1)).IsEqualTo(ErrorMessageType.AcatbilityDecoArrangFail);
    }

    [Test]
    public async Task Check_StorageCategoryCountsEveryCoffer()
    {
        var placed = new[] { Placed(1, 5, true), Placed(1, 5, true) };
        await Assert.That(Check(placed, group: 5)).IsEqualTo(ErrorMessageType.AcatbilityDecoArrangFail);
    }

    [Test]
    public async Task Check_UnlistedCategoryAndMissingLimitSet_Deny()
    {
        await Assert.That(Check([], group: 4)).IsEqualTo(ErrorMessageType.AcatbilityDecoArrangFail);
        var result = HousingDecorationRules.Check(new HousingTemplate { AbsoluteDecoLimit = 50 }, -1, 0,
            new ItemHousingDecoration(), new HousingDecoration { DecoActAbilityGroupId = 1 }, [], Data());
        await Assert.That(result).IsEqualTo(ErrorMessageType.AcatbilityDecoArrangFail);
    }

    [Test]
    public async Task Bonus_DuplicateItemsGiveOneBonusAndLegacyExcessCannotIncreaseIt()
    {
        var placed = new[] { Placed(1, 1), Placed(1, 1), Placed(2, 1), Placed(3, 1), Placed(4, 1) };
        var bonus = HousingDecorationRules.GetActAbilityBonus(11, House(), placed, Data());
        await Assert.That(bonus).IsEqualTo(300u);
    }

    [Test]
    public async Task Bonus_CategoryCapacityIsSharedAcrossProficiencies()
    {
        var placed = new[] { Placed(1, 1, actability: 12), Placed(2, 1, actability: 12),
            Placed(3, 1, actability: 12), Placed(4, 1) };
        await Assert.That(HousingDecorationRules.GetActAbilityBonus(11, House(), placed, Data())).IsEqualTo(0u);
    }

    [Test]
    public async Task Data_LoadsAllThreeTablesAndRejectsMissingReferences()
    {
        var data = Data();
        await Assert.That(data.GroupCount).IsEqualTo(3);
        await Assert.That(data.LimitCount).IsEqualTo(1);
        await Assert.That(data.GetLimit(1, 1)).IsEqualTo(3u);
        await Assert.That(data.GetLimit(1, 5)).IsEqualTo(2u);
        await Assert.That(data.GetLimit(1, 4)).IsEqualTo(0u);
        await Assert.That(() => Data(missingGroup: true)).Throws<InvalidDataException>();
    }

    private static ErrorMessageType Check(PlacedHousingDecoration[] placed, uint group = 0,
        bool restore = false, int step = -1, int allCount = -1) =>
        HousingDecorationRules.Check(House(), step, allCount < 0 ? placed.Length : allCount,
            new ItemHousingDecoration { Restore = restore }, new HousingDecoration { DecoActAbilityGroupId = group },
            placed, Data());

    private static HousingTemplate House() => new()
    {
        AbsoluteDecoLimit = 50, DecoLimit = 40, HousingDecoLimitId = 1
    };

    private static PlacedHousingDecoration Placed(uint item, uint group = 0, bool restore = false, uint actability = 11) =>
        new(item, new HousingDecoration { Id = item, DecoActAbilityGroupId = group,
            ActabilityGroupId = actability, ActabilityUp = 100 }, restore);

    private static HousingDecorationGameData Data(bool missingGroup = false)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE deco_actability_groups (id INTEGER, name TEXT);
            CREATE TABLE housing_deco_limits (id INTEGER);
            CREATE TABLE housing_deco_limit_elems (housing_deco_limit_id INTEGER, deco_actability_group_id INTEGER, count INTEGER);
            INSERT INTO deco_actability_groups VALUES (1, 'Specialty'), (4, 'Mansion'), (5, 'Storage');
            INSERT INTO housing_deco_limits VALUES (1);
            INSERT INTO housing_deco_limit_elems VALUES (1, 1, 3), (1, 5, 2);
            """;
        command.ExecuteNonQuery();
        if (missingGroup)
        {
            command.CommandText = "DELETE FROM deco_actability_groups WHERE id = 5";
            command.ExecuteNonQuery();
        }
        var data = new HousingDecorationGameData();
        data.Load(connection);
        return data;
    }
}
