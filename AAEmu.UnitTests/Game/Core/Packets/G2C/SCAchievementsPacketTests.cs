using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Achievement;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCAchievementsPacketTests
{
    [Test]
    public async Task Write_EmptySnapshot_WritesZeroCount()
    {
        var packet = new SCAchievementsPacket([]);
        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(packet.TypeId).IsEqualTo(SCOffsets.SCAchievementsPacket);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(stream.Count).IsEqualTo(4);
        await Assert.That(stream.ReadInt32()).IsEqualTo(0);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Write_TwoEntries_WritesR208022Layout()
    {
        var firstCompletion = new DateTime(2026, 8, 29, 12, 34, 56, DateTimeKind.Utc);
        var secondCompletion = new DateTime(2026, 8, 29, 13, 45, 1, DateTimeKind.Utc);
        var packet = new SCAchievementsPacket([
            new AchievementInfo { Id = 2, Amount = uint.MaxValue, Complete = firstCompletion },
            new AchievementInfo { Id = 1479, Amount = 51, Complete = secondCompletion }
        ]);
        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.Count).IsEqualTo(36);
        await Assert.That(stream.ReadInt32()).IsEqualTo(2);
        await AssertEntry(stream, 2, uint.MaxValue, firstCompletion);
        await AssertEntry(stream, 1479, 51, secondCompletion);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Encode_TwoEntries_WritesCompleteGameFrame()
    {
        var firstCompletion = new DateTime(2026, 8, 29, 12, 34, 56, DateTimeKind.Utc);
        var secondCompletion = new DateTime(2026, 8, 29, 13, 45, 1, DateTimeKind.Utc);
        var packet = new SCAchievementsPacket([
            new AchievementInfo { Id = 2, Amount = 5, Complete = firstCompletion },
            new AchievementInfo { Id = 1479, Amount = 51, Complete = secondCompletion }
        ]);

        byte[] frame = packet.Encode();

        await Assert.That(frame.Length).IsEqualTo(44);
        await Assert.That(BitConverter.ToUInt16(frame, 0)).IsEqualTo((ushort)42);
        await Assert.That(frame[2]).IsEqualTo((byte)0xdd);
        await Assert.That(frame[3]).IsEqualTo((byte)1);
        await Assert.That(frame[4]).IsEqualTo((byte)0);
        await Assert.That(frame[5]).IsEqualTo((byte)0);
        await Assert.That(BitConverter.ToUInt16(frame, 6)).IsEqualTo(SCOffsets.SCAchievementsPacket);
        await Assert.That(BitConverter.ToInt32(frame, 8)).IsEqualTo(2);
        await Assert.That(BitConverter.ToUInt32(frame, 12)).IsEqualTo(2u);
        await Assert.That(BitConverter.ToUInt32(frame, 16)).IsEqualTo(5u);
        await Assert.That(BitConverter.ToInt64(frame, 20)).IsEqualTo(Helpers.UnixTime(firstCompletion));
        await Assert.That(BitConverter.ToUInt32(frame, 28)).IsEqualTo(1479u);
        await Assert.That(BitConverter.ToUInt32(frame, 32)).IsEqualTo(51u);
        await Assert.That(BitConverter.ToInt64(frame, 36)).IsEqualTo(Helpers.UnixTime(secondCompletion));
    }

    [Test]
    public async Task Write_FiftyEntries_PreservesStableOrder()
    {
        var entries = Enumerable.Range(1, SCAchievementsPacket.MaxEntries)
            .Select(id => new AchievementInfo { Id = (uint)id, Amount = (uint)(id * 2) })
            .ToList();
        var stream = new SCAchievementsPacket(entries).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.Count).IsEqualTo(804);
        await Assert.That(stream.ReadInt32()).IsEqualTo(SCAchievementsPacket.MaxEntries);
        foreach (var entry in entries)
            await AssertEntry(stream, entry.Id, entry.Amount, DateTime.MinValue);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public void Constructor_FiftyOneEntries_RejectsOversizedSnapshot()
    {
        var entries = Enumerable.Range(1, SCAchievementsPacket.MaxEntries + 1)
            .Select(id => new AchievementInfo { Id = (uint)id })
            .ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => new SCAchievementsPacket(entries));
    }

    [Test]
    public async Task ChangedAndCompleted_Write_UseExactProgressAndCompletionTime()
    {
        const uint achievementId = 0xfedcba98;
        const int amount = 0x789abcde;
        var completedAt = new DateTime(2026, 8, 29, 14, 15, 16, DateTimeKind.Utc);

        var changedPacket = new SCAchievementChangedPacket(achievementId, amount);
        var changedStream = changedPacket.Write(new PacketStream());
        changedStream.Rollback();
        await Assert.That(changedPacket.TypeId).IsEqualTo(SCOffsets.SCAchievementChangedPacket);
        await Assert.That(changedPacket.Level).IsEqualTo((byte)1);
        await Assert.That(changedStream.Count).IsEqualTo(8);
        await Assert.That(changedStream.ReadUInt32()).IsEqualTo(achievementId);
        await Assert.That(changedStream.ReadInt32()).IsEqualTo(amount);
        await Assert.That(changedStream.LeftBytes).IsEqualTo(0);

        var completedPacket = new SCAchievementCompletedPacket(achievementId, completedAt);
        var completedStream = completedPacket.Write(new PacketStream());
        completedStream.Rollback();
        await Assert.That(completedPacket.TypeId).IsEqualTo(SCOffsets.SCAchievementCompletedPacket);
        await Assert.That(completedPacket.Level).IsEqualTo((byte)1);
        await Assert.That(completedStream.Count).IsEqualTo(12);
        await Assert.That(completedStream.ReadUInt32()).IsEqualTo(achievementId);
        await Assert.That(completedStream.ReadInt64()).IsEqualTo(Helpers.UnixTime(completedAt));
        await Assert.That(completedStream.LeftBytes).IsEqualTo(0);
    }

    private static async Task AssertEntry(
        PacketStream stream,
        uint expectedId,
        uint expectedAmount,
        DateTime expectedCompletion)
    {
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedId);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(expectedAmount);
        await Assert.That(stream.ReadInt64()).IsEqualTo(Helpers.UnixTime(expectedCompletion));
    }
}
