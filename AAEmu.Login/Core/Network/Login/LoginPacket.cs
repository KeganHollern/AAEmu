using System.Buffers;
using AAEmu.Commons.Network;
using AAEmu.Login.Core.Network.Connections;

namespace AAEmu.Login.Core.Network.Login;

public abstract class LoginPacket(ushort typeId) : PacketBase<ILoginConnection>(typeId)
{
    public void EncodeTo(IBufferWriter<byte> bufferWriter)
    {
        // TODO: Optimize to avoid unnecessary allocations
        byte[] packetStream = Encode();
        bufferWriter.Write(packetStream);
    }

    public override PacketStream Encode()
    {
        var ps = new PacketStream();
        ps.Write(new PacketStream().Write(TypeId).Write(this));
        return ps;
    }

    public override PacketBase<ILoginConnection> Decode(PacketStream ps)
    {
        Read(ps);
        return this;
    }
}
