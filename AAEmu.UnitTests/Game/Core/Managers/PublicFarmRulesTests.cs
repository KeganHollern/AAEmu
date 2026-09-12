using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class PublicFarmRulesTests
{
    [Test]
    public async Task Protection_ExpiresAtExactAuthoredMillisecond()
    {
        var planted = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        await Assert.That(PublicFarmManager.IsWithinProtection(planted, planted.AddMilliseconds(86_399_999), 86_400_000)).IsTrue();
        await Assert.That(PublicFarmManager.IsWithinProtection(planted, planted.AddDays(1), 86_400_000)).IsFalse();
        await Assert.That(PublicFarmManager.IsWithinProtection(planted, planted.AddDays(2), 86_400_000)).IsFalse();
        await Assert.That(PublicFarmManager.IsWithinProtection(planted, planted, 0)).IsFalse();
    }

    [Test]
    public async Task Protection_NoOwnerOrPublicFarm_DeniesProtection()
    {
        await Assert.That(PublicFarmManager.IsProtected(new Doodad { FarmType = FarmType.Farm }, DateTime.UtcNow)).IsFalse();
        await Assert.That(PublicFarmManager.IsProtected(new Doodad { OwnerId = 1 }, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task InfoBoard_LocalUiAction_DoesNotRequestDeletion()
    {
        var board = new Doodad { ToNextPhase = true };
        new DoodadFuncOpenFarmInfo { FarmId = 1 }.Use(null, board, 16968, -1);
        await Assert.That(board.ToNextPhase).IsFalse();
    }

    [Test]
    public async Task Load_DerivesSubzoneGroupsFromAuthoredNamesAndPreservesMilliseconds()
    {
        var data = LoadData();
        await Assert.That(data.FarmCount).IsEqualTo(3);
        await Assert.That(data.GetFarm(18).Group).IsEqualTo(FarmType.Nursery);
        await Assert.That(data.GetFarm(18).GuardTimeMilliseconds).IsEqualTo(86_400_000u);
        await Assert.That(data.GetSubzoneFarmGroup(7123)).IsEqualTo(FarmType.Farm);
        await Assert.That(data.GetSubzoneFarmGroup(4567)).IsEqualTo(FarmType.Nursery);
        await Assert.That(data.GetSubzoneFarmGroup(9999)).IsEqualTo(FarmType.Invalid);
        await Assert.That(data.GetGuardTimeMilliseconds(FarmType.Farm)).IsEqualTo(86_400_000u);
    }

    [Test]
    public async Task Load_ConflictingGuardTimesAndAmbiguousNames_Rejects()
    {
        await Assert.That(() => LoadData("UPDATE common_farms SET guard_time = 123 WHERE id = 10")).Throws<InvalidDataException>();
        await Assert.That(() => LoadData("UPDATE common_farms SET name = 'Farm' WHERE id = 18")).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ActiveCompact_LoadsEveryFarmAndAuthoredSubzoneRelation()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_COMPACT for the r208022 compact check.");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        var data = new CommonFarmGameData();
        data.Load(connection);
        await Assert.That(data.FarmCount).IsEqualTo(17);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, farm_group_id, guard_time FROM common_farms ORDER BY id";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var farm = data.GetFarm((uint)reader.GetInt32(0));
                await Assert.That((int)farm.Group).IsEqualTo(reader.GetInt32(1));
                await Assert.That(farm.GuardTimeMilliseconds).IsEqualTo((uint)reader.GetInt32(2));
                await Assert.That(data.GetGuardTimeMilliseconds(farm.Group)).IsEqualTo(86_400_000u);
            }
        }
        await Assert.That(data.GetSubzoneFarmGroup(966)).IsEqualTo(FarmType.Farm);
        await Assert.That(data.GetSubzoneFarmGroup(998)).IsEqualTo(FarmType.Farm);
        await Assert.That(data.GetSubzoneFarmGroup(967)).IsEqualTo(FarmType.Ranch);
        await Assert.That(data.GetSubzoneFarmGroup(968)).IsEqualTo(FarmType.Nursery);
        await Assert.That(data.GetSubzoneFarmGroup(974)).IsEqualTo(FarmType.Stable);
        await Assert.That(data.IsRemovedByHouse(12)).IsTrue();
        await Assert.That(data.IsRemovedByHouse(80)).IsFalse();
        await Assert.That(data.IsRemovedByHouse(uint.MaxValue)).IsFalse();
        var decorations = new HousingDecorationGameData();
        decorations.Load(connection);
        await Assert.That(decorations.GroupCount).IsEqualTo(6);
        await Assert.That(decorations.LimitCount).IsEqualTo(12);
        command.CommandText = "SELECT housing_deco_limit_id, deco_actability_group_id, count FROM housing_deco_limit_elems";
        using var limits = command.ExecuteReader();
        var count = 0;
        while (limits.Read())
        {
            await Assert.That(decorations.GetLimit((uint)limits.GetInt32(0), (uint)limits.GetInt32(1)))
                .IsEqualTo((uint)limits.GetInt32(2));
            count++;
        }
        await Assert.That(count).IsEqualTo(23);
    }

    private static CommonFarmGameData LoadData(string extra = "")
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE farm_groups (id INTEGER, count INTEGER);
            CREATE TABLE common_farms (id INTEGER, name TEXT, farm_group_id INTEGER, guard_time INTEGER);
            CREATE TABLE sub_zones (id INTEGER, name TEXT);
            CREATE TABLE farm_group_doodads (id INTEGER, farm_group_id INTEGER, doodad_id INTEGER, item_id INTEGER);
            CREATE TABLE doodad_groups (id INTEGER, guard_on_field_time INTEGER, is_export TEXT, removed_by_house TEXT);
            INSERT INTO farm_groups VALUES (1, 10), (2, 5);
            INSERT INTO common_farms VALUES (1, 'Farm', 1, 86400000), (10, 'Farm', 1, 86400000), (18, 'Nursery', 2, 86400000);
            INSERT INTO sub_zones VALUES (7123, 'Farm'), (4567, 'Nursery'), (9999, 'Town');
            """ + extra;
        command.ExecuteNonQuery();
        var data = new CommonFarmGameData();
        data.Load(connection);
        return data;
    }
}
