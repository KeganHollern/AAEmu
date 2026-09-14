using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.GameData;

public class MateGameDataTests
{
    [Test]
    public async Task ExactClient_LoadsPetSlotsAndEveryBuffMountGrant()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = compact, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var data = new MateGameData();
        data.Load(connection);
        foreach (var slot in Enum.GetValues<EquipmentItemSlot>())
        {
            await Assert.That(data.HasEquipmentSlot(1, (int)slot)).IsEqualTo(
                slot is EquipmentItemSlot.Head or EquipmentItemSlot.Waist or EquipmentItemSlot.Feet);
            await Assert.That(data.HasEquipmentSlot(2, (int)slot)).IsEqualTo(
                slot is EquipmentItemSlot.Head or EquipmentItemSlot.Chest or EquipmentItemSlot.Feet);
            await Assert.That(data.HasEquipmentSlot(3, (int)slot)).IsFalse();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT buff_id, mount_skill_id FROM buff_mount_skills";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            await Assert.That(data.BuffGrantsMountSkill((uint)reader.GetInt64(0), (uint)reader.GetInt64(1))).IsTrue();
            count++;
        }
        await Assert.That(count).IsEqualTo(64);
        await Assert.That(data.BuffGrantsMountSkill(1863, 30)).IsTrue();
        await Assert.That(data.BuffGrantsMountSkill(1863, 83)).IsFalse();
        await Assert.That(data.IsUnderwaterModel(1233)).IsTrue();
        await Assert.That(data.IsUnderwaterModel(139)).IsFalse();
    }

    [Test]
    public async Task Load_UsesExactMountRowsBuffListsAndPetEquipmentMetadata()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE npc_mount_skills(id INTEGER, npc_id INTEGER, mount_skill_id INTEGER);
            CREATE TABLE mount_skills(id INTEGER, name TEXT, skill_id INTEGER);
            CREATE TABLE mount_attached_skills(id INTEGER, mount_skill_id INTEGER, attach_point_id INTEGER, skill_id INTEGER);
            CREATE TABLE buff_mount_skills(buff_id INTEGER, mount_skill_id INTEGER);
            CREATE TABLE mate_equip_slot_packs(id INTEGER, head TEXT, chest TEXT, waist TEXT, feet TEXT);
            CREATE TABLE models(id INTEGER, sub_id INTEGER, sub_type TEXT);
            CREATE TABLE actor_models(id INTEGER, underwater_creature TEXT);
            INSERT INTO npc_mount_skills VALUES(1,3599,83);
            INSERT INTO mount_skills VALUES(83,'speed',17092),(999,'same base, other seat',17092);
            INSERT INTO mount_attached_skills VALUES(1,83,1,18228),(2,999,2,17718);
            INSERT INTO buff_mount_skills VALUES(1863,999);
            INSERT INTO mate_equip_slot_packs VALUES(1,'t','f','t','t'),(2,'t','t','f','t');
            INSERT INTO models VALUES(139,42,'ActorModel'),(1233,538,'ActorModel');
            INSERT INTO actor_models VALUES(42,'f'),(538,'t');
            """;
        command.ExecuteNonQuery();
        var data = new MateGameData();
        data.Load(connection);

        await Assert.That(data.GetMateSkills(3599)).IsEquivalentTo(new[] { 83u });
        await Assert.That(data.GetSkillId(999)).IsEqualTo(17092u);
        await Assert.That(data.TryGetRiderSkill(83, AttachPointKind.Driver, out var rider)).IsTrue();
        await Assert.That(rider).IsEqualTo(18228u);
        await Assert.That(data.TryGetRiderSkill(999, AttachPointKind.Driver, out _)).IsFalse();
        await Assert.That(data.TryGetRiderSkill(999, AttachPointKind.Passenger0, out rider)).IsTrue();
        await Assert.That(rider).IsEqualTo(17718u);
        await Assert.That(data.BuffGrantsMountSkill(1863,999)).IsTrue();
        await Assert.That(data.BuffGrantsMountSkill(1863,83)).IsFalse();
        await Assert.That(data.HasEquipmentSlot(1, (int)EquipmentItemSlot.Waist)).IsTrue();
        await Assert.That(data.HasEquipmentSlot(1, (int)EquipmentItemSlot.Chest)).IsFalse();
        await Assert.That(data.IsUnderwaterModel(1233)).IsTrue();
        await Assert.That(data.IsUnderwaterModel(139)).IsFalse();
    }
}
