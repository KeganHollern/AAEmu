using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

public sealed class CSSaveUserMusicNotesPacketTests
{
    [Test]
    public async Task Body_PreservesNativeFieldsAndRemovesOnlyTransportTerminator()
    {
        const string song = "MML@t120 é𝄞,c;\r\n";
        var body = Body(Encoding.UTF8.GetBytes("Étude"), [.. Encoding.UTF8.GetBytes(song), 0]);
        var packet = new CSSaveUserMusicNotesPacket();
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x122);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(body, out var request)).IsTrue();
        await Assert.That(request.ItemId).IsEqualTo(0x0123456789abcdefUL);
        await Assert.That(request.Container).IsEqualTo(SlotType.Inventory);
        await Assert.That(request.Slot).IsEqualTo((byte)173);
        await Assert.That(request.Title).IsEqualTo("Étude");
        await Assert.That(request.Song).IsEqualTo(song);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Body_RejectsEveryTruncatedLengthAndTrailingData()
    {
        var bytes = Body("Title"u8.ToArray(), "cdef;\0"u8.ToArray()).GetBytes();
        for (var length = 0; length < bytes.Length; length++)
        {
            var body = new PacketStream(bytes[..length]);
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(body, out var request)).IsFalse();
            await Assert.That(request.ItemId).IsEqualTo(0UL);
        }
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(new PacketStream([.. bytes, 0]), out _)).IsFalse();
    }

    [Test]
    public async Task Body_RejectsWrongByteLengthAndEmptyItemId()
    {
        foreach (var length in new[] { -1, 0, 1, 3, 12001, int.MaxValue })
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(
                Body("Title"u8.ToArray(), [0xc3, 0xa9, 0], length), out _)).IsFalse();
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(
            Body("Title"u8.ToArray(), "c\0"u8.ToArray(), itemId: 0), out _)).IsFalse();
    }

    [Test]
    public async Task Body_RequiresOneTerminalNulOnlyForNotes()
    {
        foreach (var notes in new byte[][] { [], [0], [99], [99, 0, 0], [0, 99, 0], [99, 0, 100, 0] })
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body("Title"u8.ToArray(), notes), out _)).IsFalse();
        foreach (var title in new byte[][] { [], [0], [65, 0], [0, 65], [65, 0, 66] })
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body(title, "c\0"u8.ToArray()), out _)).IsFalse();
    }

    [Test]
    public async Task Body_RejectsMalformedUtf8InEitherField()
    {
        foreach (var invalid in new byte[][] { [0x80], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82] })
        {
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body(invalid, "c\0"u8.ToArray()), out _)).IsFalse();
            await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body("Title"u8.ToArray(), [.. invalid, 0]), out _)).IsFalse();
        }
    }

    [Test]
    public async Task Body_UsesNativeByteCapacitiesBeforeCharacterRankValidation()
    {
        var title = Encoding.UTF8.GetBytes(new string('é', 48));
        var song = Encoding.UTF8.GetBytes(new string('c', 12000));
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body(title, [.. song, 0]), out _)).IsTrue();
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body([.. title, 65], [99, 0]), out _)).IsFalse();
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(Body(title, [.. song, 99, 0]), out _)).IsFalse();

        var excessivePrefix = Body("Title"u8.ToArray(), [99, 0]).GetBytes();
        excessivePrefix[14] = 0xff;
        excessivePrefix[15] = 0xff;
        await Assert.That(CSSaveUserMusicNotesPacket.TryReadRequest(new PacketStream(excessivePrefix), out _)).IsFalse();
    }

    private static PacketStream Body(byte[] title, byte[] terminatedSong, int? songSize = null,
        ulong itemId = 0x0123456789abcdefUL)
    {
        return new PacketStream().Write(songSize ?? terminatedSong.Length - 1).Write(itemId)
            .Write((byte)SlotType.Inventory).Write((byte)173)
            .Write((ushort)title.Length).Write(title, false)
            .Write((ushort)terminatedSong.Length).Write(terminatedSong, false);
    }
}
