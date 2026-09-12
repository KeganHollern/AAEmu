using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;

namespace AAEmu.UnitTests.Commons.Network;

public class PacketStreamBoundsTests
{
    [Test]
    public async Task ReadUInt16_IncompleteHeader_PreservesBytesForNextReceive()
    {
        var stream = new PacketStream(new byte[] { 0x34 });

        Assert.Throws<MarshalException>(() => stream.ReadUInt16());
        await Assert.That(stream.Pos).IsEqualTo(0);
        await Assert.That(stream.Count).IsEqualTo(1);

        stream.PushBack((byte)0x12);

        await Assert.That(stream.ReadUInt16()).IsEqualTo((ushort)0x1234);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task PrimitiveReads_EmptyStream_ThrowWithoutAdvancing()
    {
        Action<PacketStream>[] reads =
        [
            stream => stream.ReadBoolean(),
            stream => stream.ReadByte(),
            stream => stream.ReadSByte(),
            stream => stream.ReadBytes(1),
            stream => stream.ReadBytes(),
            stream => stream.ReadChar(),
            stream => stream.ReadChars(1),
            stream => stream.ReadInt16(),
            stream => stream.ReadInt32(),
            stream => stream.ReadInt64(),
            stream => stream.ReadUInt16(),
            stream => stream.ReadUInt32(),
            stream => stream.ReadUInt64(),
            stream => stream.ReadBc(),
            stream => stream.ReadSingle(),
            stream => stream.ReadDouble(),
            stream => stream.ReadPacketStream(),
            stream => stream.Read(new PacketStream())
        ];

        foreach (var read in reads)
        {
            var stream = new PacketStream();
            Assert.Throws<MarshalException>(() => read(stream));
            await Assert.That(stream.Pos).IsEqualTo(0);
        }
    }

    [Test]
    public void ComplexReads_EmptyStream_PropagateBoundsFailure()
    {
        Action<PacketStream>[] reads =
        [
            stream => stream.Read(new StringMarshaler()),
            stream => stream.Read<StringMarshaler>(),
            stream => stream.ReadCollection<StringMarshaler>(),
            stream => stream.ReadDateTime(),
            stream => stream.ReadPiscW(5),
            stream => stream.ReadPosition(),
            stream => stream.ReadQuaternionShort(),
            stream => stream.ReadVector3Single(),
            stream => stream.ReadVector3Short(),
            stream => stream.ReadString(),
            stream => stream.ReadString(1)
        ];

        foreach (var read in reads)
            Assert.Throws<MarshalException>(() => read(new PacketStream()));
    }

    [Test]
    public void ReadString_TruncatedPayload_DoesNotReturnAnEmptyString()
    {
        var stream = new PacketStream().Write((short)3).Write(new byte[] { 0x61, 0x62 });

        Assert.Throws<MarshalException>(() => stream.ReadString());
    }

    [Test]
    public void ReadCollection_TruncatedElement_StopsBeforeTheNextElement()
    {
        var stream = new PacketStream().Write(2).Write((short)1);

        Assert.Throws<MarshalException>(() => stream.ReadCollection<StringMarshaler>());
    }

    [Test]
    public void VariableLengthReads_InvalidLengths_ThrowBoundsFailure()
    {
        var stream = new PacketStream(new byte[] { 1, 2, 3, 4 });

        Assert.Throws<MarshalException>(() => stream.ReadBytes(-1));
        Assert.Throws<MarshalException>(() => stream.ReadBytes(int.MaxValue));
        Assert.Throws<MarshalException>(() => stream.ReadChars(-1));
        Assert.Throws<MarshalException>(() => stream.ReadChars(int.MaxValue));
        Assert.Throws<MarshalException>(() => new PacketStream().Write((short)-1).ReadBytes());
        Assert.Throws<MarshalException>(() => new PacketStream().Write((short)-1).ReadPacketStream());
    }

    [Test]
    public async Task ZeroLengthReads_AtEndOfStream_RemainValid()
    {
        var stream = new PacketStream();

        await Assert.That(stream.ReadBytes(0).Length).IsEqualTo(0);
        await Assert.That(stream.ReadChars(0).Length).IsEqualTo(0);
        await Assert.That(stream.ReadString(0)).IsEqualTo(string.Empty);
    }

    public sealed class StringMarshaler : PacketMarshaler
    {
        public override void Read(PacketStream stream)
        {
            stream.ReadString();
        }
    }
}
