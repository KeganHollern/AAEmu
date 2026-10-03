using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Shipyard;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class ShipyardSalvageTests
{
    [Test]
    public async Task ReadSalvageRewards_PreservesAuthoredRowsAndGroups()
    {
        using var connection = CreateRewards("(7, 7, 1330, 't', 12, 6), (1, 5, 1330, 't', 26, 10), (10, 7, 99, 'f', 3, 2)");
        var rewards = ShipyardManager.ReadSalvageRewards(connection);

        await Assert.That(rewards.Count).IsEqualTo(2);
        await Assert.That(rewards[5].Single().Count).IsEqualTo(10);
        await Assert.That(rewards[5].Single().Radius).IsEqualTo(26f);
        await Assert.That(rewards[7].Select(row => row.Id)).IsEquivalentTo(new uint[] { 7, 10 });
        await Assert.That(rewards[7][0].DoodadId).IsEqualTo(1330u);
        await Assert.That(rewards[7][0].OnWater).IsTrue();
        await Assert.That(rewards[7][1].OnWater).IsFalse();
    }

    [Test]
    [Arguments(0, 1330, 12, 6)]
    [Arguments(7, 0, 12, 6)]
    [Arguments(7, 1330, -1, 6)]
    [Arguments(7, 1330, 12, 0)]
    [Arguments(7, 1330, 12, -1)]
    public async Task ReadSalvageRewards_InvalidRow_RejectsBeforeWorldMutation(int shipyard, int doodad, int radius, int count)
    {
        using var connection = CreateRewards($"(1, {shipyard}, {doodad}, 't', {radius}, {count})");
        await Assert.That(() => ShipyardManager.ReadSalvageRewards(connection)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GetPosition_UsesAuthoredSurfaceAndRadius(bool onWater)
    {
        var reward = new ShipyardReward { Id = 7, DoodadId = 1330, Radius = 12, Count = 6, OnWater = onWater };
        var origin = new Vector3(1200, 2400, 100);
        var random = new Random(714);
        for (var index = 0; index < 100; index++)
        {
            var position = reward.GetPosition(origin, random, (_, water) => water ? 42 : 24);
            await Assert.That(Vector2.Distance(new(origin.X, origin.Y), new(position.X, position.Y))).IsLessThanOrEqualTo(12f);
            await Assert.That(position.Z).IsEqualTo(onWater ? 42f : 24f);
        }
    }

    [Test]
    public async Task GetPosition_ZeroRandomAndZeroRadius_RemainsFinite()
    {
        var reward = new ShipyardReward { Radius = 0 };
        var position = reward.GetPosition(new Vector3(20, 30, 40), new ZeroRandom(), (_, _) => 50);
        await Assert.That(position).IsEqualTo(new Vector3(20, 30, 50));
    }

    [Test]
    public async Task GetPosition_InvalidHeight_RejectsBeforePersistence()
    {
        var reward = new ShipyardReward { Id = 1, Radius = 12 };
        await Assert.That(() => reward.GetPosition(Vector3.Zero, new Random(7), (_, _) => float.NaN))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Cancel_RepeatedRollback_ReleasesEachAllocationOnce()
    {
        var doodads = new List<Doodad>
        {
            new() { DbId = 51, ObjId = 61, IsPersistent = true },
            new() { DbId = 52, ObjId = 62, IsPersistent = true }
        };
        var released = new List<uint>();
        var salvage = new ShipyardManager.ShipyardSalvage(doodads, doodad => released.Add(doodad.DbId));

        salvage.Cancel();
        salvage.Cancel();
        salvage.Publish();

        await Assert.That(released).IsEquivalentTo(new uint[] { 51, 52 });
        await Assert.That(doodads.All(doodad => !doodad.IsPersistent)).IsTrue();
        await Assert.That(() => salvage.Save(null)).Throws<InvalidOperationException>();
    }

    private static SqliteConnection CreateRewards(string values)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE shipyard_rewards (id INT, shipyard_id INT, doodad_id INT, on_water NUM, radius REAL, count INT); " +
            $"INSERT INTO shipyard_rewards VALUES {values}";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed class ZeroRandom : Random
    {
        public override double NextDouble() => 0;
    }
}
