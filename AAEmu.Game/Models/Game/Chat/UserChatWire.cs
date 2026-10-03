using System.Text;

using AAEmu.Commons.Network;

namespace AAEmu.Game.Models.Game.Chat;

internal static class UserChatWire
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    internal static string ReadString(PacketStream stream, int maximumBytes)
    {
        var size = stream.ReadUInt16();
        if (size > maximumBytes || size > stream.LeftBytes)
            throw new InvalidDataException("Invalid chat string length.");
        try
        {
            var value = s_utf8.GetString(stream.ReadBytes(size));
            if (value.Contains('\0'))
                throw new InvalidDataException("A chat string contains a NUL character.");
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Invalid chat UTF-8.", exception);
        }
    }

    internal static void End(PacketStream stream)
    {
        if (stream.LeftBytes != 0)
            throw new InvalidDataException("Unexpected trailing chat bytes.");
    }
}
