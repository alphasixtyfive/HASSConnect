using System.Net;
using System.Security.Cryptography;
using System.Text;
using HassConnect.Updates;

namespace HassConnect.Tests;

public sealed class UpdateCheckerTests
{
    private const string InstallerName = "HASSConnect-1.3.0-Setup-x64.exe";

    [Fact]
    public async Task EmptyRepositoryDoesNotContactNetwork()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected network request")));
        var result = await new UpdateChecker(client).CheckAsync(null, "0.1.0");
        Assert.Equal(UpdateState.NotConfigured, result.State);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", UpdateState.Available)]
    [InlineData("v0.1.0", "0.1.0", UpdateState.Current)]
    [InlineData("v0.1.0", "0.2.0", UpdateState.Current)]
    [InlineData("v0.2.0-beta", "0.1.0", UpdateState.Unavailable)]
    public async Task ComparesReleaseVersions(string tag, string current, UpdateState expected)
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"draft":false,"prerelease":false,"tag_name":"{{tag}}"}""")
        }));
        var result = await new UpdateChecker(client).CheckAsync("example/app", current);
        Assert.Equal(expected, result.State);
        if (expected == UpdateState.Available) Assert.Equal("https://github.com/example/app/releases/tag/v0.2.0", result.ReleasePage!.AbsoluteUri);
    }

    [Fact]
    public async Task MissingReleaseIsNotReportedAsUpToDate()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        Assert.Equal(UpdateState.Unavailable, (await new UpdateChecker(client).CheckAsync("example/app", "0.1.0")).State);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"draft\":false,\"prerelease\":true,\"tag_name\":\"v0.2.0\"}")]
    [InlineData("{\"draft\":true,\"prerelease\":false,\"tag_name\":\"v0.2.0\"}")]
    [InlineData("{\"draft\":\"false\",\"prerelease\":false,\"tag_name\":\"v0.2.0\"}")]
    [InlineData("{\"draft\":false,\"prerelease\":false,\"tag_name\":42}")]
    [InlineData("null")]
    public async Task InvalidOrPreviewReleaseIsNotOffered(string payload)
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        var result = await new UpdateChecker(client).CheckAsync("example/app", "0.1.0");
        Assert.Equal(UpdateState.Unavailable, result.State);
        Assert.Null(result.ReleasePage);
    }

    [Fact]
    public async Task IdentifiesActualVersionAndPinsApiContract()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("HASSConnect/1.2.3", request.Headers.UserAgent.ToString());
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Equal("https://api.github.com/repos/example/app/releases/latest", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.NotFound);
        }));
        await new UpdateChecker(client).CheckAsync("example/app", "1.2.3");
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("\"1234\"", false)]
    [InlineData("0", false)]
    [InlineData("536870913", false)]
    public async Task OnlyOffersTrustedInstallerAssetsWithAValidSize(string size, bool canDownload)
    {
        var payload = $$"""
            {
              "draft": false,
              "prerelease": false,
              "tag_name": "v1.3.0",
              "assets": [
                { "name": "HASSConnect-1.3.0-Setup-x64.exe", "size": {{size}}, "browser_download_url": "https://github.com/example/app/releases/download/v1.3.0/HASSConnect-1.3.0-Setup-x64.exe" },
                { "name": "SHA256SUMS.txt", "size": 100, "browser_download_url": "https://github.com/example/app/releases/download/v1.3.0/SHA256SUMS.txt" }
              ]
            }
            """;
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        }));

        var result = await new UpdateChecker(client).CheckAsync("example/app", "1.2.3");

        Assert.Equal(UpdateState.Available, result.State);
        if (!canDownload)
        {
            Assert.Null(result.Package);
            Assert.NotNull(result.ReleasePage);
            return;
        }
        Assert.NotNull(result.Package);
        Assert.Equal("1.3.0", result.Package.Version);
        Assert.Equal(1234, result.Package.Size);
    }

    [Fact]
    public async Task RejectsInstallerAssetsOutsideTheConfiguredRepository()
    {
        const string payload = """
            {
              "draft": false,
              "prerelease": false,
              "tag_name": "v1.3.0",
              "assets": [
                { "name": "HASSConnect-1.3.0-Setup-x64.exe", "size": 1234, "browser_download_url": "https://attacker.example/setup.exe" },
                { "name": "SHA256SUMS.txt", "size": 100, "browser_download_url": "https://github.com/example/app/releases/download/v1.3.0/SHA256SUMS.txt" }
              ]
            }
            """;
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        }));

        var result = await new UpdateChecker(client).CheckAsync("example/app", "1.2.3");

        Assert.Equal(UpdateState.Available, result.State);
        Assert.Null(result.Package);
        Assert.Contains("installer is missing", result.Message);
    }

    [Fact]
    public async Task DownloadsAndVerifiesInstallerThroughGitHubAssetRedirects()
    {
        var bytes = Encoding.UTF8.GetBytes("verified installer content");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var package = Package(bytes.Length);
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "github.com")
                return new(HttpStatusCode.Found) { Headers = { Location = new("https://release-assets.githubusercontent.com" + uri.AbsolutePath) } };
            Assert.Equal("release-assets.githubusercontent.com", uri.Host);
            return uri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {InstallerName}\n") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var directory = Path.Combine(Path.GetTempPath(), $"hass-connect-update-test-{Guid.NewGuid():N}");
        try
        {
            var result = await new UpdateInstaller(client).DownloadAsync(package, "example/app", directory);

            Assert.Equal(hash, result.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.InstallerPath));
            Assert.Equal(result.InstallerPath, Assert.Single(Directory.EnumerateFiles(directory)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DeletesPartialFileWhenChecksumDoesNotMatch()
    {
        var bytes = Encoding.UTF8.GetBytes("tampered installer content");
        var package = Package(bytes.Length);
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? new(HttpStatusCode.OK) { Content = new StringContent($"{new string('0', 64)}  HASSConnect-1.3.0-Setup-x64.exe\n") }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var directory = Path.Combine(Path.GetTempPath(), $"hass-connect-update-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdateInstaller(client).DownloadAsync(package, "example/app", directory));

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RejectsRedirectOutsideGitHubWithoutFollowingIt()
    {
        var package = Package(1);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(package.ChecksumDownload, request.RequestUri);
            return new(HttpStatusCode.Found) { Headers = { Location = new("https://downloads.example/SHA256SUMS.txt") } };
        }));
        var directory = Path.Combine(Path.GetTempPath(), $"hass-connect-update-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdateInstaller(client).DownloadAsync(package, "example/app", directory));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task RejectsInstallerWhenResponseLengthDiffersFromReleaseSize()
    {
        byte[] bytes = [1, 2, 3];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var package = Package(bytes.Length + 1);
        using var client = new HttpClient(new Handler(request => request.RequestUri == package.ChecksumDownload
            ? new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {InstallerName}\n") }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var directory = Path.Combine(Path.GetTempPath(), $"hass-connect-update-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdateInstaller(client).DownloadAsync(package, "example/app", directory));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task StopsReadingAnOversizedChecksumWithoutContentLength()
    {
        const int maximumBytes = 64 * 1024;
        const string repository = "example/app";
        var package = Package(1);
        using var body = new UnseekableStream(new byte[maximumBytes * 2]);
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(package.ChecksumDownload, request.RequestUri);
            return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
        }));
        var directory = Path.Combine(Path.GetTempPath(), $"hass-connect-update-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UpdateInstaller(client).DownloadAsync(package, repository, directory));
            Assert.Equal(maximumBytes + 1, body.BytesRead);
        }
        finally { Directory.Delete(directory); }
    }

    private static UpdatePackage Package(long size) => new(
        "1.3.0",
        new($"https://github.com/example/app/releases/download/v1.3.0/{InstallerName}"),
        new("https://github.com/example/app/releases/download/v1.3.0/SHA256SUMS.txt"),
        size);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ExplainsRateLimits(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        }));
        var result = await new UpdateChecker(client).CheckAsync("example/app", "1.2.3");
        Assert.Equal(UpdateState.Unavailable, result.State);
        Assert.Contains("request limit", result.Message);
    }

    [Fact]
    public async Task ReportsNetworkFailureWithoutOfferingARelease()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException()));
        var result = await new UpdateChecker(client).CheckAsync("example/app", "1.2.3");
        Assert.Equal(UpdateState.Unavailable, result.State);
        Assert.Null(result.ReleasePage);
    }

    [Fact]
    public async Task DistinguishesTimeoutFromCallerCancellation()
    {
        using var client = new HttpClient(new Handler(_ => throw new TaskCanceledException()));
        var checker = new UpdateChecker(client);
        Assert.Contains("timed out", (await checker.CheckAsync("example/app", "1.2.3")).Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync("example/app", "1.2.3", cancellation.Token));
    }

    [Fact]
    public async Task RejectsInvalidConfigurationBeforeContactingGitHub()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected request")));
        var checker = new UpdateChecker(client);
        Assert.Equal(UpdateState.Unavailable, (await checker.CheckAsync("example/app?token=secret", "1.2.3")).State);
        Assert.Equal(UpdateState.Unavailable, (await checker.CheckAsync("example/app", "1.2.3.4")).State);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
