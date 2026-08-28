using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.Game.Core.Network.Stream;

public abstract class StreamPacket(ushort typeId) : PacketBase<StreamConnection>(typeId)
{
    public override PacketStream Encode()
    {
        var ps = new PacketStream();
        try
        {
            ps.Write(new PacketStream().Write(TypeId).Write(this));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StreamPacket: Failed to encode S->C type {0:X3}", TypeId);
            throw;
        }

        var logLevel = LogLevel;
        if (IsLogLevelEnabled(logLevel))
        {
            var logString = $"StreamPacket: S->C type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
            LogPacket(logLevel, logString);
        }

        return ps;
    }

    public override PacketBase<StreamConnection> Decode(PacketStream ps)
    {
        Read(ps);

        var logLevel = LogLevel;
        if (IsLogLevelEnabled(logLevel))
        {
            var logString = $"StreamPacket: C->S type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
            LogPacket(logLevel, logString);
        }

        return this;
    }
}
