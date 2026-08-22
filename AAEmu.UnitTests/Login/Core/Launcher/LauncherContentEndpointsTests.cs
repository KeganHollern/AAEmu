#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AAEmu.Login.Core.Controllers;
using AAEmu.Login.Core.Launcher;
using AAEmu.Login.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Login.Core.Launcher;

public class LauncherContentEndpointsTests
{
    [Test]
    public async Task ContentRoutes_Disabled_AreNotMapped()
    {
        await using var application = await TestApplication.StartAsync(contentEnabled: false);

        var response = await application.SendAuthenticatedAsync("/launcher/v2/manifest");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ContentRoutes_AuthenticatePreserveBytesAndSupportRanges()
    {
        await using var application = await TestApplication.StartAsync();

        var unauthenticated = await application.Client.GetAsync("/launcher/v2/manifest");
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var manifest = await application.SendAuthenticatedAsync("/launcher/v2/manifest");
        await Assert.That(manifest.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await manifest.Content.ReadAsByteArrayAsync())
            .SequenceEqual(application.ContentProvider.ManifestBytes.ToArray())).IsTrue();
        await Assert.That(manifest.Headers.ETag?.Tag)
            .IsEqualTo($"\"sha256-{application.ContentProvider.ManifestSha256}\"");
        await Assert.That(manifest.Headers.Location).IsNull();
        await Assert.That(manifest.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(manifest.Headers.GetValues("X-Content-Type-Options").Single())
            .IsEqualTo("nosniff");

        var minisig = await application.SendAuthenticatedAsync("/launcher/v2/manifest.minisig");
        await Assert.That(minisig.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await minisig.Content.ReadAsByteArrayAsync())
            .SequenceEqual(application.ContentProvider.MinisigBytes.ToArray())).IsTrue();

        using var rangeRequest = application.AuthenticatedRequest(
            HttpMethod.Get, $"/launcher/v2/assets/{application.Asset.Sha256}");
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        var range = await application.Client.SendAsync(rangeRequest);
        await Assert.That(range.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
        await Assert.That((await range.Content.ReadAsByteArrayAsync())
            .SequenceEqual(application.AssetBytes[2..6])).IsTrue();
        await Assert.That(range.Content.Headers.ContentRange?.ToString())
            .IsEqualTo($"bytes 2-5/{application.AssetBytes.Length}");
        await Assert.That(range.Headers.ETag?.Tag)
            .IsEqualTo($"\"sha256-{application.Asset.Sha256}\"");

        var uppercase = await application.SendAuthenticatedAsync(
            $"/launcher/v2/assets/{application.Asset.Sha256.ToUpperInvariant()}");
        await Assert.That(uppercase.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var unknown = await application.SendAuthenticatedAsync(
            $"/launcher/v2/assets/{new string('f', 64)}");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var expired = new HttpRequestMessage(HttpMethod.Get, "/launcher/v2/manifest");
        expired.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "expired");
        var expiredResponse = await application.Client.SendAsync(expired);
        await Assert.That(expiredResponse.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private sealed class TestApplication : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _root;

        private TestApplication(
            WebApplication application,
            HttpClient client,
            string root,
            StubContentProvider contentProvider,
            ClientContentAsset asset,
            byte[] assetBytes)
        {
            _application = application;
            Client = client;
            _root = root;
            ContentProvider = contentProvider;
            Asset = asset;
            AssetBytes = assetBytes;
        }

        public HttpClient Client { get; }
        public StubContentProvider ContentProvider { get; }
        public ClientContentAsset Asset { get; }
        public byte[] AssetBytes { get; }

        public static async Task<TestApplication> StartAsync(bool contentEnabled = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"aaemu-content-http-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var assetBytes = Encoding.ASCII.GetBytes("0123456789");
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(assetBytes));
            var assetPath = Path.Combine(root, sha256);
            await File.WriteAllBytesAsync(assetPath, assetBytes);
            var asset = new ClientContentAsset(
                sha256,
                assetBytes.LongLength,
                () => new MemoryStream(assetBytes, writable: false));
            var contentProvider = new StubContentProvider(
                Encoding.ASCII.GetBytes("{\"schemaVersion\":2}\n"),
                Encoding.ASCII.GetBytes("test minisig\n"),
                asset);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production"
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LauncherApi:Enabled"] = "true",
                ["LauncherApi:ClientCompactPath"] = assetPath,
                ["LauncherApi:ExpectedClientCompactSha256"] = new string('1', 64),
                ["LauncherApi:ExpectedClientCompactSize"] = "10",
                ["LauncherApi:ContentV2:Enabled"] = contentEnabled.ToString(),
                ["LauncherApi:ContentV2:ReleasePath"] = root,
                ["LauncherApi:ContentV2:ExpectedManifestSha256"] = new string('2', 64),
                ["LauncherApi:ContentV2:ExpectedMinisigSha256"] = new string('3', 64)
            });
            builder.Services.AddLauncherApi();
            var readiness = new LoginReadiness();
            readiness.MarkInitialized();
            builder.Services.AddSingleton<ILoginReadiness>(readiness);
            builder.Services.AddSingleton<IClientCompactProvider>(new StubCompactProvider(assetPath));
            builder.Services.AddSingleton<IClientContentBundleProvider>(contentProvider);
            builder.Services.AddSingleton<ILauncherSessionService, StubSessionService>();
            builder.Services.AddSingleton(Mock.Of<ILoginController>().Object);
            builder.Services.AddSingleton(Mock.Of<IGameController>().Object);
            builder.Services.AddSingleton(Mock.Of<ILaunchTicketService>().Object);

            var application = builder.Build();
            application.MapLauncherApi();
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Test server has no address feature");
            var client = new HttpClient { BaseAddress = new Uri(addresses.Single()) };
            return new TestApplication(application, client, root, contentProvider, asset, assetBytes);
        }

        public HttpRequestMessage AuthenticatedRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            return request;
        }

        public async Task<HttpResponseMessage> SendAuthenticatedAsync(string path)
        {
            using var request = AuthenticatedRequest(HttpMethod.Get, path);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubSessionService : ILauncherSessionService
    {
        public Task<LauncherSessionPrincipal?> AuthenticateAccessTokenAsync(
            string token, CancellationToken cancellationToken)
        {
            LauncherSessionPrincipal? principal = token == "valid"
                ? new LauncherSessionPrincipal(1, new AccountId(1), "tester")
                : null;
            return Task.FromResult(principal);
        }

        public Task<LauncherSessionTokens> CreateAsync(
            AccountId accountId, string username, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LauncherSessionTokens?> RefreshAsync(
            string refreshToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevokeAsync(ulong sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsActiveAsync(
            ulong sessionId, AccountId accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCompactProvider(string path) : IClientCompactProvider
    {
        public bool IsAvailable => true;
        public string FilePath => path;
        public ClientCompactManifestResponse Manifest { get; } = new(
            1, new string('1', 64), new string('1', 64), 10, "/launcher/v1/assets/client.sqlite3");
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubContentProvider : IClientContentBundleProvider
    {
        private readonly ClientContentAsset _asset;

        public StubContentProvider(byte[] manifestBytes, byte[] minisigBytes, ClientContentAsset asset)
        {
            ManifestBytes = manifestBytes;
            MinisigBytes = minisigBytes;
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            MinisigSha256 = Convert.ToHexStringLower(SHA256.HashData(minisigBytes));
            _asset = asset;
        }

        public bool IsAvailable => true;
        public ReadOnlyMemory<byte> ManifestBytes { get; }
        public ReadOnlyMemory<byte> MinisigBytes { get; }
        public string ManifestSha256 { get; }
        public string MinisigSha256 { get; }

        public bool TryGetAsset(
            string sha256, [NotNullWhen(true)] out ClientContentAsset? asset)
        {
            asset = sha256 == _asset.Sha256 ? _asset : null;
            return asset is not null;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
