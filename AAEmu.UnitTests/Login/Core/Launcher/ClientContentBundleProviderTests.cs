#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Login.Core.Launcher;

public class ClientContentBundleProviderTests
{
    [Test]
    public void AddLauncherApi_InvalidV2Activation_FailsOptionsValidation()
    {
        AssertInvalidOptions(new Dictionary<string, string?>
        {
            ["LauncherApi:Enabled"] = "false",
            ["LauncherApi:ContentV2:Enabled"] = "true",
            ["LauncherApi:ContentV2:ReleasePath"] = "/release",
            ["LauncherApi:ContentV2:ExpectedManifestSha256"] = new string('1', 64),
            ["LauncherApi:ContentV2:ExpectedMinisigSha256"] = new string('2', 64)
        });
        AssertInvalidOptions(new Dictionary<string, string?>
        {
            ["LauncherApi:Enabled"] = "true",
            ["LauncherApi:ClientCompactPath"] = "/compact",
            ["LauncherApi:ExpectedClientCompactSha256"] = new string('1', 64),
            ["LauncherApi:ExpectedClientCompactSize"] = "2",
            ["LauncherApi:ContentV2:Enabled"] = "true",
            ["LauncherApi:ContentV2:ReleasePath"] = "/release",
            ["LauncherApi:ContentV2:ExpectedManifestSha256"] = new string('0', 64),
            ["LauncherApi:ContentV2:ExpectedMinisigSha256"] = new string('2', 64)
        });
    }

    [Test]
    public async Task InitializeAsync_Disabled_DoesNotReadConfiguredPath()
    {
        using var provider = CreateProvider(new LauncherApiOptions
        {
            ContentV2 = new LauncherContentV2Options
            {
                Enabled = false,
                ReleasePath = "/definitely/missing"
            }
        });

        await provider.InitializeAsync(CancellationToken.None);

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_ValidBundle_PublishesRawBytesAndAllowlistedAssets()
    {
        using var bundle = await TestBundle.CreateAsync();
        using var provider = CreateProvider(bundle.Options);

        await provider.InitializeAsync(CancellationToken.None);

        await Assert.That(provider.IsAvailable).IsTrue();
        await Assert.That(provider.ManifestBytes.ToArray().SequenceEqual(bundle.ManifestBytes)).IsTrue();
        await Assert.That(provider.MinisigBytes.ToArray().SequenceEqual(bundle.MinisigBytes)).IsTrue();
        await Assert.That(provider.ManifestSha256).IsEqualTo(Sha256(bundle.ManifestBytes));
        foreach (var (sha256, contents) in bundle.Blobs)
        {
            await Assert.That(provider.TryGetAsset(sha256, out var asset)).IsTrue();
            await Assert.That(asset).IsNotNull();
            await Assert.That(asset!.Sha256).IsEqualTo(sha256);
            await Assert.That(asset.Size).IsEqualTo(contents.LongLength);
            await using var stream = asset.OpenReadStream();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            await Assert.That(copy.ToArray().SequenceEqual(contents)).IsTrue();
        }
        await Assert.That(provider.TryGetAsset(new string('f', 64), out _)).IsFalse();
    }

    [Test]
    public async Task OpenReadStream_PathReplacedAfterVerification_ServesVerifiedObject()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var bundle = await TestBundle.CreateAsync();
        using var provider = CreateProvider(bundle.Options);
        await provider.InitializeAsync(CancellationToken.None);
        var (sha256, contents) = bundle.Blobs.First();
        var path = Path.Combine(bundle.Root, "blobs", sha256);
        File.Move(path, $"{path}.replaced");
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xff, contents.Length).ToArray());

        await Assert.That(provider.TryGetAsset(sha256, out var asset)).IsTrue();
        await using var stream = asset!.OpenReadStream();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        await Assert.That(copy.ToArray().SequenceEqual(contents)).IsTrue();

        await using var range = asset.OpenReadStream();
        range.Seek(2, SeekOrigin.Begin);
        var rangeBytes = new byte[4];
        await range.ReadExactlyAsync(rangeBytes);
        await Assert.That(rangeBytes.SequenceEqual(contents[2..6])).IsTrue();
    }

    [Test]
    public async Task InitializeAsync_ParentTraversalInReleasePath_FailsWithoutPublishing()
    {
        using var bundle = await TestBundle.CreateAsync();
        bundle.Options.ContentV2.ReleasePath = Path.Combine(bundle.Root, "blobs", "..");
        using var provider = CreateProvider(bundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_SymlinkedReleaseRoot_FailsWithoutPublishing()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var bundle = await TestBundle.CreateAsync();
        var link = Path.Combine(Path.GetTempPath(), $"aaemu-content-link-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(link, bundle.Root);
        try
        {
            bundle.Options.ContentV2.ReleasePath = link;
            using var provider = CreateProvider(bundle.Options);

            Assert.Throws<InvalidOperationException>(
                () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

            await Assert.That(provider.IsAvailable).IsFalse();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Test]
    public async Task InitializeAsync_ChangedManifestPin_FailsWithoutPublishing()
    {
        using var bundle = await TestBundle.CreateAsync();
        bundle.Options.ContentV2.ExpectedManifestSha256 = new string('f', 64);
        using var provider = CreateProvider(bundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_UppercaseBlobReference_FailsWithoutPublishing()
    {
        using var bundle = await TestBundle.CreateAsync();
        var sha256 = bundle.Blobs.Keys.First();
        bundle.RewriteManifest(
            Encoding.ASCII.GetString(bundle.ManifestBytes)
                .Replace(sha256, sha256.ToUpperInvariant(), StringComparison.Ordinal));
        using var provider = CreateProvider(bundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_DuplicateJsonProperty_FailsWithoutPublishing()
    {
        using var bundle = await TestBundle.CreateAsync();
        bundle.RewriteManifest(
            Encoding.ASCII.GetString(bundle.ManifestBytes)
                .Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2",
                    StringComparison.Ordinal));
        using var provider = CreateProvider(bundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_ExtraOrCorruptBlob_FailsWithoutPublishing()
    {
        using var extraBundle = await TestBundle.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(extraBundle.Root, "blobs", "extra"), [1]);
        using var extraProvider = CreateProvider(extraBundle.Options);
        Assert.Throws<InvalidOperationException>(
            () => extraProvider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(extraProvider.IsAvailable).IsFalse();

        using var corruptBundle = await TestBundle.CreateAsync();
        var (sha256, contents) = corruptBundle.Blobs.First();
        await File.WriteAllBytesAsync(
            Path.Combine(corruptBundle.Root, "blobs", sha256),
            Enumerable.Repeat((byte)0xff, contents.Length).ToArray());
        using var corruptProvider = CreateProvider(corruptBundle.Options);
        Assert.Throws<InvalidOperationException>(
            () => corruptProvider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(corruptProvider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_OversizedSignature_FailsBeforePublishing()
    {
        using var bundle = await TestBundle.CreateAsync();
        bundle.MinisigBytes = new byte[16 * 1024 + 1];
        await File.WriteAllBytesAsync(Path.Combine(bundle.Root, "manifest.minisig"), bundle.MinisigBytes);
        bundle.Options.ContentV2.ExpectedMinisigSha256 = Sha256(bundle.MinisigBytes);
        using var provider = CreateProvider(bundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => provider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    private static void AssertInvalidOptions(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLauncherApi();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<LauncherApiOptions>>().Value);
    }

    private static ClientContentBundleProvider CreateProvider(LauncherApiOptions options)
    {
        return new ClientContentBundleProvider(
            Options.Create(options),
            Mock.Of<ILogger<ClientContentBundleProvider>>().Object);
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed class TestBundle : IDisposable
    {
        private TestBundle(
            string root,
            byte[] manifestBytes,
            byte[] minisigBytes,
            Dictionary<string, byte[]> blobs,
            LauncherApiOptions options)
        {
            Root = root;
            ManifestBytes = manifestBytes;
            MinisigBytes = minisigBytes;
            Blobs = blobs;
            Options = options;
        }

        public string Root { get; }
        public byte[] ManifestBytes { get; private set; }
        public byte[] MinisigBytes { get; set; }
        public Dictionary<string, byte[]> Blobs { get; }
        public LauncherApiOptions Options { get; }

        public static async Task<TestBundle> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"aaemu-content-v2-{Guid.NewGuid():N}");
            var blobsPath = Path.Combine(root, "blobs");
            Directory.CreateDirectory(blobsPath);
            var values = new[]
            {
                Encoding.ASCII.GetBytes("compact"),
                Encoding.ASCII.GetBytes("render"),
                Encoding.ASCII.GetBytes("system"),
                Encoding.ASCII.GetBytes("sparse")
            };
            var blobs = values.ToDictionary(Sha256, value => value, StringComparer.Ordinal);
            foreach (var (sha256, value) in blobs)
                await File.WriteAllBytesAsync(Path.Combine(blobsPath, sha256), value);

            var entries = blobs.Select((entry, index) => new
            {
                representation = new
                {
                    kind = index == 3 ? "sparse-v1" : "full",
                    blob = new { sha256 = entry.Key, size = entry.Value.LongLength }
                }
            }).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                product = "aaemu-r208022-client",
                channel = "production",
                launcherProtocolFloor = 2,
                artifacts = entries
            }) + "\n";
            var manifestBytes = Encoding.ASCII.GetBytes(json);
            var minisigBytes = Encoding.ASCII.GetBytes("untrusted test signature\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "manifest.json"), manifestBytes);
            await File.WriteAllBytesAsync(Path.Combine(root, "manifest.minisig"), minisigBytes);
            var options = new LauncherApiOptions
            {
                Enabled = true,
                ContentV2 = new LauncherContentV2Options
                {
                    Enabled = true,
                    ReleasePath = root,
                    ExpectedManifestSha256 = Sha256(manifestBytes),
                    ExpectedMinisigSha256 = Sha256(minisigBytes)
                }
            };
            return new TestBundle(root, manifestBytes, minisigBytes, blobs, options);
        }

        public void RewriteManifest(string text)
        {
            ManifestBytes = Encoding.ASCII.GetBytes(text);
            File.WriteAllBytes(Path.Combine(Root, "manifest.json"), ManifestBytes);
            Options.ContentV2.ExpectedManifestSha256 = Sha256(ManifestBytes);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
