namespace AAEmu.Game.Core.Packets.L2G;

public static class LGOffsets
{
    // All opcodes here are updated for version client_12_r208022
    public const ushort LGRegisterGameServerPacket = 0x000;
    public const ushort LGPlayerEnterPacket = 0x001;
    public const ushort LGPlayerReconnectPacket = 0x002;
    public const ushort LGRequestInfoPacket = 0x003;
    // Cluster-private Login/Game extensions, not r208022 client opcodes.
    public const ushort LGModerationResultPacket = 0x005;
    public const ushort LGModerationStatePacket = 0x006;
}
