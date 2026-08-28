using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.Game.Core.Network.Game;

public abstract class GamePacket(ushort typeId, byte level) : PacketBase<GameConnection>(typeId)
{
    public byte Level { get; set; } = level;

    /// <summary>
    /// This is called in Encode after Read() in the case of GamePackets
    /// The purpose is to separate packet data from packet behavior
    /// </summary>
    public virtual void Execute() { }

    public override PacketStream Encode()
    {
        var ps = new PacketStream();
        try
        {
            var packet = new PacketStream()
                .Write((byte)0xdd)
                .Write(Level);

            var body = new PacketStream()
                .Write(TypeId)
                .Write(this);

            if (Level == 1)
            {
                packet
                    .Write((byte)0) // hash
                    .Write((byte)0); // count
            }

            packet.Write(body, false);

            ps.Write(packet);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "GamePacket: Failed to encode S->C type {0:X3}", TypeId);
            throw;
        }

        var logLevel = LogLevel;
        if (IsLogLevelEnabled(logLevel))
        {
            var logString = $"GamePacket: S->C type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
            LogPacket(logLevel, logString);
        }

        return ps;
    }

    public override PacketBase<GameConnection> Decode(PacketStream ps)
    {
        Read(ps);

        var logLevel = LogLevel;
        if (IsLogLevelEnabled(logLevel))
        {
            var logString = $"GamePacket: C->S type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
            LogPacket(logLevel, logString);
        }

        Execute();
        return this;
    }
}
