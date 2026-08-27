#nullable enable

using System.Security.Cryptography;
using System.Text;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Login.Core.Launcher;

public class LauncherUpdateBundleProviderTests
{
    [Test]
    public void AddLauncherApi_EnabledUpdateWithoutPinnedFiles_FailsOptionsValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LauncherUpdate:Enabled"] = "true"
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLauncherApi();
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = serviceProvider.GetRequiredService<IOptions<LauncherUpdateOptions>>().Value);
    }

    [Test]
    public async Task InitializeAsync_UpdateDisabled_DoesNotReadConfiguredPath()
    {
        var provider = CreateProvider(new LauncherUpdateOptions
        {
            ReleasePath = "/definitely/missing"
        });

        await provider.InitializeAsync(CancellationToken.None);

        await Assert.That(provider.IsAvailable).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_ValidPinnedFiles_PublishesExactFiles()
    {
        using var bundle = await TestBundle.CreateAsync();
        var provider = CreateProvider(bundle.Options);

        await provider.InitializeAsync(CancellationToken.None);

        await Assert.That(provider.IsAvailable).IsTrue();
        foreach (var (asset, expectedBytes) in new[]
                 {
                     (provider.Manifest, bundle.Files[LauncherUpdateBundleProvider.ManifestFileName]),
                     (provider.Minisig, bundle.Files[LauncherUpdateBundleProvider.MinisigFileName]),
                     (provider.LinuxArchive, bundle.Files[LauncherUpdateBundleProvider.LinuxArchiveFileName]),
                     (provider.WindowsArchive, bundle.Files[LauncherUpdateBundleProvider.WindowsArchiveFileName])
                 })
        {
            await Assert.That(asset.Size).IsEqualTo(expectedBytes.LongLength);
            await Assert.That(asset.Sha256).IsEqualTo(Sha256(expectedBytes));
            await using var stream = asset.OpenReadStream();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            await Assert.That(copy.ToArray().SequenceEqual(expectedBytes)).IsTrue();
        }
    }

    [Test]
    public async Task InitializeAsync_SizeOrShaPinMismatch_FailsWithoutPublishing()
    {
        using var sizeBundle = await TestBundle.CreateAsync();
        sizeBundle.Options.ExpectedLinuxArchiveSize++;
        var sizeProvider = CreateProvider(sizeBundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => sizeProvider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(sizeProvider.IsAvailable).IsFalse();

        using var shaBundle = await TestBundle.CreateAsync();
        shaBundle.Options.ExpectedWindowsArchiveSha256 = new string('f', 64);
        var shaProvider = CreateProvider(shaBundle.Options);

        Assert.Throws<InvalidOperationException>(
            () => shaProvider.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(shaProvider.IsAvailable).IsFalse();
    }

    private static LauncherUpdateBundleProvider CreateProvider(LauncherUpdateOptions options)
    {
        return new LauncherUpdateBundleProvider(
            Options.Create(options),
            Mock.Of<ILogger<LauncherUpdateBundleProvider>>().Object);
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed class TestBundle : IDisposable
    {
        private TestBundle(
            string root,
            Dictionary<string, byte[]> files,
            LauncherUpdateOptions options)
        {
            Root = root;
            Files = files;
            Options = options;
        }

        public string Root { get; }
        public Dictionary<string, byte[]> Files { get; }
        public LauncherUpdateOptions Options { get; }

        public static async Task<TestBundle> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"aaemu-launcher-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [LauncherUpdateBundleProvider.ManifestFileName] =
                    Encoding.ASCII.GetBytes("{\"schemaVersion\":1}\n"),
                [LauncherUpdateBundleProvider.MinisigFileName] =
                    Encoding.ASCII.GetBytes("test signature\n"),
                [LauncherUpdateBundleProvider.LinuxArchiveFileName] =
                    Encoding.ASCII.GetBytes("linux archive"),
                [LauncherUpdateBundleProvider.WindowsArchiveFileName] =
                    Encoding.ASCII.GetBytes("windows archive")
            };
            foreach (var (fileName, bytes) in files)
                await File.WriteAllBytesAsync(Path.Combine(root, fileName), bytes);

            var options = new LauncherUpdateOptions
            {
                Enabled = true,
                ReleasePath = root,
                ExpectedManifestSize = files[LauncherUpdateBundleProvider.ManifestFileName].LongLength,
                ExpectedManifestSha256 = Sha256(files[LauncherUpdateBundleProvider.ManifestFileName]),
                ExpectedMinisigSize = files[LauncherUpdateBundleProvider.MinisigFileName].LongLength,
                ExpectedMinisigSha256 = Sha256(files[LauncherUpdateBundleProvider.MinisigFileName]),
                ExpectedLinuxArchiveSize = files[LauncherUpdateBundleProvider.LinuxArchiveFileName].LongLength,
                ExpectedLinuxArchiveSha256 = Sha256(files[LauncherUpdateBundleProvider.LinuxArchiveFileName]),
                ExpectedWindowsArchiveSize = files[LauncherUpdateBundleProvider.WindowsArchiveFileName].LongLength,
                ExpectedWindowsArchiveSha256 = Sha256(files[LauncherUpdateBundleProvider.WindowsArchiveFileName])
            };
            return new TestBundle(root, files, options);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
