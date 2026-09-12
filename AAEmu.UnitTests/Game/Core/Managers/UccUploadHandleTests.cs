using AAEmu.Game.Models.Stream;

namespace AAEmu.UnitTests.Game.Core.Managers;

public sealed class UccUploadHandleTests
{
    [Test]
    [Arguments(1u, 1u, 1u, 0x31545844u)]
    [Arguments(32u, 64u, 7u, 0x33545844u)]
    [Arguments(256u, 256u, 9u, 0x35545844u)]
    [Arguments(128u, 256u, 4u, 0x32495441u)]
    public async Task NativeDds_AcceptsSupportedDimensionsFormatsAndMipCounts(uint width, uint height, uint mipCount, uint format)
    {
        var data = UccPurchaseTests.Dds(width, height, mipCount, format);
        await Assert.That(UccUploadHandle.IsValidDds(data)).IsTrue();
        var ucc = new CustomUcc();
        var handle = new UccUploadHandle(data.Length, ucc);
        for (var offset = 0; offset < data.Length; offset += UccUploadHandle.PartSize)
        {
            var part = data.Skip(offset).Take(UccUploadHandle.PartSize).ToArray();
            await Assert.That(handle.TryAddPart(new UccPart { Total = data.Length, Size = part.Length,
                Index = (uint)(offset / UccUploadHandle.PartSize), Data = part })).IsTrue();
        }
        await Assert.That(handle.TryFinalizeUpload()).IsTrue();
        await Assert.That(ucc.Data.SequenceEqual(data)).IsTrue();
    }

    [Test]
    [Arguments(0, 0u)]
    [Arguments(4, 120u)]
    [Arguments(12, 0u)]
    [Arguments(16, 3u)]
    [Arguments(16, 512u)]
    [Arguments(24, 2u)]
    [Arguments(28, 0u)]
    [Arguments(28, 2u)]
    [Arguments(76, 0u)]
    [Arguments(80, 0u)]
    [Arguments(84, 0u)]
    [Arguments(112, 512u)]
    public async Task InvalidDdsHeader_RejectsWithoutPublishingData(int offset, uint value)
    {
        var data = UccPurchaseTests.Dds();
        UccPurchaseTests.Put(data, offset, value);
        await Assert.That(UccUploadHandle.IsValidDds(data)).IsFalse();
    }

    [Test]
    [Arguments("total")]
    [Arguments("index")]
    [Arguments("size")]
    [Arguments("duplicate")]
    [Arguments("short")]
    [Arguments("overrun")]
    public async Task InvalidPart_RejectsWithoutIncreasingProgress(string reason)
    {
        var data = UccPurchaseTests.Dds();
        var source = new CustomUcc();
        var handle = new UccUploadHandle(data.Length, source);
        var part = new UccPart { Total = data.Length, Size = data.Length, Data = data };
        if (reason == "total") part.Total++;
        if (reason == "index") part.Index = 1;
        if (reason == "size") part.Size++;
        if (reason == "short") { part.Data = data[..^1]; part.Size--; }
        if (reason == "overrun") { part.Data = [.. data, 0]; part.Size++; }
        if (reason == "duplicate") await Assert.That(handle.TryAddPart(part)).IsTrue();
        await Assert.That(handle.TryAddPart(part)).IsFalse();
        await Assert.That(source.Data.Count).IsEqualTo(0);
        await Assert.That(handle.UploadComplete).IsEqualTo(reason == "duplicate");
    }

    [Test]
    public async Task MaximumDds_RejectsAnExtraByte()
    {
        var data = UccPurchaseTests.Dds(256, 256, 9);
        await Assert.That(data.Length).IsEqualTo(UccUploadHandle.MaximumDdsSize);
        await Assert.That(UccUploadHandle.IsValidDds([.. data, 0])).IsFalse();
    }
}
