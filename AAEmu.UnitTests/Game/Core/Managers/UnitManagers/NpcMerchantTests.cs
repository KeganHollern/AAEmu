using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.UnitManagers;

public sealed class NpcMerchantTests
{
    [Test]
    public async Task LoadMerchantData_RetainsSharedGoodsAndEmptyPersonalPack()
    {
        using var connection = CreateData();
        var manager = CreateManager();
        var template = new NpcTemplate { Id = 626, Merchant = true };
        manager.GetAllTemplates().Add(template.Id, template);

        manager.LoadMerchantData(connection);

        await Assert.That(template.MerchantPackIds).IsEquivalentTo(new uint[] { 9, 154 });
        await Assert.That(manager.GetGoods(9).Items).IsEmpty();
        var goods = manager.GetNpcGoods(template);
        await Assert.That(goods.Items.Select(item => item.ItemTemplateId))
            .IsEquivalentTo(new uint[] { 23090, 23091 });
        await Assert.That(goods.Items.Select(item => item.Grade)).IsEquivalentTo(new byte[] { 0, 2 });
    }

    [Test]
    public async Task GetNpcGoods_CombinesPacksWithoutChangingTheSharedPack()
    {
        using var connection = CreateData();
        var manager = CreateManager();
        var template = new NpcTemplate { Id = 627, Merchant = true };
        manager.GetAllTemplates().Add(template.Id, template);
        manager.LoadMerchantData(connection);

        var goods = manager.GetNpcGoods(template);

        await Assert.That(goods.Items.Select(item => item.ItemTemplateId))
            .IsEquivalentTo(new uint[] { 23090, 23091, 23092 });
        await Assert.That(goods.Items.Count).IsEqualTo(3);
        await Assert.That(manager.GetGoods(154).Items.Count).IsEqualTo(2);
        await Assert.That(manager.GetGoods(155).Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetNpcGoods_IgnoresMissingPackButRejectsMixedCurrencyPacks()
    {
        using var connection = CreateData();
        var manager = CreateManager();
        manager.LoadMerchantData(connection);

        await Assert.That(manager.GetNpcGoods(new NpcTemplate())).IsNull();
        await Assert.That(manager.GetNpcGoods(new NpcTemplate { MerchantPackIds = [999] })).IsNull();
        await Assert.That(manager.GetNpcGoods(new NpcTemplate { MerchantPackIds = [154, 999] }).Items.Count).IsEqualTo(2);
        await Assert.That(manager.GetNpcGoods(new NpcTemplate { MerchantPackIds = [154, 156] })).IsNull();
    }

    [Test]
    public async Task ExactCompact_AllMultiPackNpcGoodsRemainAvailable()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_MERCHANT_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_MERCHANT_COMPACT to check the r208022 merchant data.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var manager = CreateManager();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT npc_id FROM merchants";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = (uint)reader.GetInt64(0);
                manager.GetAllTemplates().Add(id, new NpcTemplate { Id = id, Merchant = true });
            }
        }
        manager.LoadMerchantData(connection);

        var multiplePackNpcs = manager.GetAllTemplates().Values
            .Where(template => template.MerchantPackIds.Count > 1).ToList();
        await Assert.That(multiplePackNpcs.Count).IsEqualTo(38);
        foreach (var template in multiplePackNpcs)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT DISTINCT merchant_goods.item_id, merchant_goods.grade_id
                                  FROM merchants
                                  JOIN merchant_goods ON merchant_goods.merchant_pack_id = merchants.merchant_pack_id
                                  WHERE merchants.npc_id = $npc
                                  """;
            command.Parameters.AddWithValue("$npc", template.Id);
            using var reader = command.ExecuteReader();
            var expected = new List<(uint ItemId, byte Grade)>();
            while (reader.Read())
                expected.Add(((uint)reader.GetInt64(0), reader.GetByte(1)));
            var goods = manager.GetNpcGoods(template);
            await Assert.That(goods).IsNotNull();
            await Assert.That(goods.Items.Select(item => (item.ItemTemplateId, item.Grade)))
                .IsEquivalentTo(expected);
        }
        await Assert.That(manager.GetNpcGoods(manager.GetTemplate(626)).Items.Count).IsEqualTo(26);
    }

    private static NpcManager CreateManager()
    {
        return new NpcManager(Mock.Of<IObjectIdManager>().Object, Mock.Of<IModelManager>().Object,
            Mock.Of<IFactionManager>().Object, Mock.Of<IItemManager>().Object, Mock.Of<IAIManager>().Object);
    }

    private static SqliteConnection CreateData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE merchants(id INTEGER, npc_id INTEGER, merchant_pack_id INTEGER);
                              CREATE TABLE merchant_packs(id INTEGER, kind_id INTEGER);
                              CREATE TABLE merchant_goods(id INTEGER, merchant_pack_id INTEGER, item_id INTEGER, grade_id INTEGER);
                              INSERT INTO merchants VALUES(363,626,154),(9,626,9),(11,627,154),(12,627,155),(13,999,154);
                              INSERT INTO merchant_packs VALUES(9,0),(154,0),(155,0),(156,1);
                              INSERT INTO merchant_goods VALUES(1,154,23090,0),(2,154,23091,2),
                                  (3,155,23091,2),(4,155,23092,4),(5,156,23090,0);
                              """;
        command.ExecuteNonQuery();
        return connection;
    }
}
