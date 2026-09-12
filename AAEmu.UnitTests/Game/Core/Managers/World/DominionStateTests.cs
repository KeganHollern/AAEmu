using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Dominions;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed class DominionStateTests
{
    [Test]
    public async Task AuthoredTerritories_LoadsAllSixUnclaimedZoneGroups()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE siege_zones (id INTEGER, zone_group_id INTEGER);
            INSERT INTO siege_zones VALUES (1,33),(2,34),(3,43),(4,44),(6,54),(7,56);
            """;
        command.ExecuteNonQuery();
        var authored = DominionManager.LoadAuthored(connection);
        await Assert.That(authored.Keys.ToArray()).IsEquivalentTo(new ushort[] { 33, 34, 43, 44, 54, 56 });
        foreach (var (zone, siege) in authored)
        {
            var state = DominionState.Unclaimed(zone, siege);
            await Assert.That(state.OwnerExpeditionId).IsEqualTo(0u);
            await Assert.That(state.TaxRate).IsEqualTo(0);
            await Assert.That(state.HouseTaxBalance).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task UnclaimedData_WritesCompleteR208022BodyBeforeTaxRate()
    {
        var state = DominionState.Unclaimed(33, 1);
        var packet = new SCDominionDataPacket(state.ToPacketData(), false, false);
        var bytes = packet.Write(new PacketStream()).GetBytes();
        // Native FUN_3983a470 and its nested serializers produce 258 data bytes.
        // G2C 0x1d adds newlyDeclared and finalDataByRequest, both false here.
        var expected = new byte[260];
        expected[0] = 33;
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x1d);
        await Assert.That(bytes.SequenceEqual(expected)).IsTrue();
        var rate = new SCDominionTaxRatePacket(33, 0).Write(new PacketStream());
        rate.Rollback();
        await Assert.That(rate.ReadUInt16()).IsEqualTo((ushort)33);
        await Assert.That(rate.ReadInt32()).IsEqualTo(0);
        await Assert.That(rate.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownOwnership_CannotPublishPartialClaimData()
    {
        var state = DominionState.Unclaimed(33, 1) with { OwnerExpeditionId = 100 };
        await Assert.That(() => state.ToPacketData()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DormantRequests_ReadConfirmedBodiesWithoutAnActiveCharacter()
    {
        var craft = new PacketStream();
        craft.WriteBc(0x123456u);
        craft.Write(4567);
        craft.Rollback();
        var craftPacket = new CSSetCraftingPayPacket();
        craftPacket.Read(craft);
        await Assert.That(craftPacket.TypeId).IsEqualTo((ushort)0x08d);
        await Assert.That(craft.LeftBytes).IsEqualTo(0);

        var national = new PacketStream();
        national.Write((ushort)33);
        national.Write(500);
        national.Rollback();
        var nationalPacket = new CSUpdateNationalTaxRatePacket();
        nationalPacket.Read(national);
        await Assert.That(nationalPacket.TypeId).IsEqualTo((ushort)0x013);
        await Assert.That(national.LeftBytes).IsEqualTo(0);
    }
}
