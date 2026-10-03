using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Music;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSaveUserMusicNotesPacket() : GamePacket(CSOffsets.CSSaveUserMusicNotesPacket, 1)
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    public override void Read(PacketStream stream)
    {
        var player = Connection.ActiveChar;
        if (player == null)
            return;

        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!TryReadRequest(stream, out var upload) || !HasMatchingSource(player, upload))
            {
                MusicManager.Instance.RejectUpload(player);
                return;
            }
            MusicManager.Instance.UploadSong(player, upload.Title, upload.Song, upload.ItemId);
        }
    }

    internal static bool HasMatchingSource(Character player, MusicNotesUpload upload)
    {
        var item = player.Inventory?.GetItemById(upload.ItemId);
        return upload.Container == SlotType.Inventory && item != null && item.OwnerId == player.Id &&
            ReferenceEquals(item._holdingContainer, player.Inventory.Bag) && item.Slot == upload.Slot;
    }

    internal static bool TryReadRequest(PacketStream stream, out MusicNotesUpload upload)
    {
        upload = default;
        if (stream.LeftBytes < 14)
            return false;
        var songBytes = stream.ReadInt32();
        var itemId = stream.ReadUInt64();
        var container = (SlotType)stream.ReadByte();
        var slot = stream.ReadByte();
        if (songBytes is <= 0 or > MusicNoteRules.MaximumSongBytes || itemId == 0 ||
            !TryReadText(stream, MusicNoteRules.MaximumTitleBytes, false, out var title, out _) ||
            !TryReadText(stream, MusicNoteRules.MaximumSongBytes, true, out var song, out var actualBytes) ||
            actualBytes != songBytes || stream.LeftBytes != 0)
            return false;

        upload = new MusicNotesUpload(itemId, container, slot, title, song);
        return true;
    }

    private static bool TryReadText(PacketStream stream, int maximumBytes, bool terminated,
        out string text, out int textBytes)
    {
        text = null;
        textBytes = 0;
        if (stream.LeftBytes < 2)
            return false;
        var wireLength = stream.ReadUInt16();
        textBytes = wireLength - (terminated ? 1 : 0);
        if (textBytes <= 0 || textBytes > maximumBytes || stream.LeftBytes < wireLength)
            return false;
        var bytes = stream.ReadBytes(wireLength);
        if ((terminated && bytes[^1] != 0) || bytes.AsSpan(0, textBytes).Contains((byte)0))
            return false;
        try
        {
            text = s_utf8.GetString(bytes, 0, textBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

internal readonly record struct MusicNotesUpload(ulong ItemId, SlotType Container, byte Slot, string Title, string Song);
