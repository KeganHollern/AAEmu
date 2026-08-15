#nullable enable

using System.Security.Cryptography;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Login.Core.Launcher;

public class ClientCompactProviderTests
{
    [Test]
    public async Task InitializeAsync_ExpectedFile_PublishesVerifiedManifest()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"aaemu-compact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var path = Path.Combine(tempDirectory, "client.sqlite3");
            var contents = new byte[64];
            "SQLite format 3\0"u8.CopyTo(contents);
            await File.WriteAllBytesAsync(path, contents);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(contents));
            var provider = new ClientCompactProvider(
                Options.Create(new LauncherApiOptions
                {
                    Enabled = true,
                    ClientCompactPath = path,
                    ExpectedClientCompactSha256 = sha256,
                    ExpectedClientCompactSize = contents.Length
                }),
                Mock.Of<ILogger<ClientCompactProvider>>().Object);

            await provider.InitializeAsync(CancellationToken.None);

            await Assert.That(provider.IsAvailable).IsTrue();
            await Assert.That(provider.FilePath).IsEqualTo(path);
            await Assert.That(provider.Manifest).IsEqualTo(new ClientCompactManifestResponse(
                1, sha256, sha256, contents.Length, "/launcher/v1/assets/client.sqlite3"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
