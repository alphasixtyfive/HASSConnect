using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HassConnect.Core;

namespace HassConnect.Updates;

public enum UpdateState { NotConfigured, Current, Available, Unavailable }
public sealed record UpdatePackage(string Version, Uri InstallerDownload, Uri ChecksumDownload, long? Size);
public sealed record UpdateResult(
    UpdateState State,
    string Message,
    Uri? ReleasePage = null,
    UpdatePackage? Package = null);

/// <summary>Checks published stable GitHub releases without downloading or executing installers.</summary>
public sealed partial class UpdateChecker(HttpClient client)
{
    public async Task<UpdateResult> CheckAsync(string? repository, string currentVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return new(UpdateState.NotConfigured, "Updates aren’t configured yet.");
        if (!RepositoryNamePattern().IsMatch(repository) ||
            !ReleaseVersion.TryParse(currentVersion, out var current))
            return new(UpdateState.Unavailable, "The update configuration is invalid.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"HASSConnect/{current.ToString(3)}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateState.Unavailable, "No published release is available yet.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
                return new(UpdateState.Unavailable, "GitHub’s request limit was reached. Try again later.");
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var release = payload.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                return new(UpdateState.Unavailable, "No stable release is available yet.");
            var tag = release.GetProperty("tag_name").GetString();
            if (!ReleaseVersion.TryParseTag(tag, out var latest))
                return new(UpdateState.Unavailable, "The release version isn’t recognized.");
            if (latest <= current)
                return new(UpdateState.Current, "You’re up to date.");

            var version = latest.ToString(3);
            var releasePage = new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag!)}");
            var package = FindPackage(release, repository, tag!, version);
            return package is null
                ? new(UpdateState.Available,
                    $"Version {version} is available, but its Windows installer is missing.", releasePage)
                : new(UpdateState.Available, $"Version {version} is available.", releasePage, package);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateState.Unavailable, "The update check timed out. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(UpdateState.Unavailable, "Couldn’t check for updates. Try again.");
        }
    }

    private static UpdatePackage? FindPackage(JsonElement release, string repository, string tag, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var installerName = $"HASSConnect-{version}-Setup-x64.exe";
        const string checksumName = "SHA256SUMS.txt";
        Uri? installer = null;
        Uri? checksum = null;
        long? size = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !asset.TryGetProperty("browser_download_url", out var urlElement) ||
                urlElement.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (!IsTrustedAsset(url, repository, tag, name!))
                continue;
            if (string.Equals(name, installerName, StringComparison.Ordinal))
            {
                installer = url;
                if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value))
                    size = value;
            }
            else if (string.Equals(name, checksumName, StringComparison.Ordinal))
            {
                checksum = url;
            }
        }

        return installer is not null && checksum is not null && size is > 0
            ? new(version, installer, checksum, size)
            : null;
    }

    internal static bool IsTrustedAsset(Uri uri, string repository, string tag, string name)
    {
        var expectedPath = $"/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        return uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex RepositoryNamePattern();
}
