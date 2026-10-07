using System.Text;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSaveUIDataPacket() : GamePacket(CSOffsets.CSSaveUIDataPacket, 1)
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    public override void Read(PacketStream stream)
    {
        var player = Connection.ActiveChar;
        if (player == null || !Connection.IsAuthenticated || Connection.IsClosed ||
            player.AccountId != Connection.AccountId || !TryReadData(stream, out var key, out var ownerId, out var data) ||
            ownerId != player.Id)
        {
            Connection.Shutdown();
            return;
        }
        lock (SaveManager.PersistenceSyncRoot)
        {
            player.SetOption(key, data);
            // The shared timer coalesces repeated values. Logout also saves all UI data.
            player.SaveOption(key);
        }
    }

    internal static bool TryReadData(PacketStream stream, out ushort key, out uint ownerId, out string data)
    {
        key = 0;
        ownerId = 0;
        data = null;
        if (stream.LeftBytes < 8)
            return false;
        key = stream.ReadUInt16();
        ownerId = stream.ReadUInt32();
        var length = stream.ReadUInt16();
        if (!CharacterUiData.IsServerKey(key) || ownerId == 0 || length > CharacterUiData.MaximumBytes || stream.LeftBytes != length)
            return false;
        var bytes = stream.ReadBytes(length);
        if (bytes.AsSpan().Contains((byte)0))
            return false;
        try
        {
            data = s_utf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
