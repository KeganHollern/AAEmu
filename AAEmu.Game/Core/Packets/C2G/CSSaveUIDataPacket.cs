using System.Buffers.Binary;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSaveUIDataPacket() : GamePacket(CSOffsets.CSSaveUIDataPacket, 1)
{
    private const int HeaderSize = sizeof(ushort) + sizeof(uint) + sizeof(ushort);

    public override void Read(PacketStream stream)
    {
        var player = Connection.ActiveChar;
        if (player == null || !Connection.IsAuthenticated || Connection.IsClosed || player.AccountId != Connection.AccountId)
        {
            Connection.Shutdown();
            return;
        }
        var start = stream.Pos;
        var bodyLength = stream.LeftBytes;
        var validDocument = TryReadData(stream, out var key, out var ownerId, out var data);
        if (bodyLength < HeaderSize || bodyLength > HeaderSize + CharacterUiData.MaximumBytes ||
            !CharacterUiData.IsServerKey(key) || ownerId == 0 || ownerId != player.Id)
        {
            Connection.Shutdown();
            return;
        }
        if (!validDocument)
        {
            // The client saves preferences before requesting Select Character. Keep its last
            // good option and let the session finish the switch if one document is unreadable.
            if (Connection.StateRejectionEvents.TryConsume())
            {
                var body = stream.Buffer.AsSpan(start, bodyLength);
                var declared = BinaryPrimitives.ReadUInt16LittleEndian(body[(HeaderSize - sizeof(ushort))..]);
                var document = body[HeaderSize..];
                Logger.Info("Skipped UI save for key {Key}: declared {DeclaredBytes} bytes, received {DocumentBytes}, first NUL at {NulOffset}",
                    key, declared, document.Length, document.IndexOf((byte)0));
            }
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
        if (stream.LeftBytes < HeaderSize)
            return false;
        key = stream.ReadUInt16();
        ownerId = stream.ReadUInt32();
        var length = stream.ReadUInt16();
        if (!CharacterUiData.IsServerKey(key) || ownerId == 0 || length > CharacterUiData.MaximumBytes || stream.LeftBytes != length)
            return false;
        return CharacterUiData.TryDecode(key, stream.ReadBytes(length), out data);
    }
}
