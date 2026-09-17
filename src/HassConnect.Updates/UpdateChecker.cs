using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HassConnect.Core;

namespace HassConnect.Updates;

public enum UpdateState { NotConfigured, Current, Available, Unavailable }
public sealed record UpdateResult(UpdateState State, string Message, Uri? ReleasePage = null);

/// <summary>Checks published stable GitHub releases without downloading or executing installers.</summary>
public sealed class UpdateChecker(HttpClient client)
{
    public async Task<UpdateResult> CheckAsync(string? repository, string currentVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return new(UpdateState.NotConfigured, "Updates aren’t configured yet.");
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z") ||
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
            return latest > current
                ? new(UpdateState.Available, $"Version {latest.ToString(3)} is available.",
                    new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag!)}"))
                : new(UpdateState.Current, "You’re up to date.");
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

}
