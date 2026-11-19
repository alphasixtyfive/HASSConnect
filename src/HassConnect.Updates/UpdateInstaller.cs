using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HassConnect.Core;

namespace HassConnect.Updates;

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes);
public sealed record DownloadedUpdate(string Version, string InstallerPath, string Sha256);

/// <summary>Downloads a release installer and verifies it against the published SHA-256 manifest.</summary>
public sealed partial class UpdateInstaller(HttpClient client)
{
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumChecksumBytes = 64 * 1024;
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    public async Task<DownloadedUpdate> DownloadAsync(
        UpdatePackage package,
        string repository,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!ReleaseVersion.TryParse(package.Version, out _))
            throw new InvalidDataException("The selected update version is invalid.");
        var tag = $"v{package.Version}";
        var installerName = $"HASSConnect-{package.Version}-Setup-x64.exe";
        if (!UpdateChecker.IsTrustedAsset(package.InstallerDownload, repository, tag, installerName) ||
            !UpdateChecker.IsTrustedAsset(package.ChecksumDownload, repository, tag, "SHA256SUMS.txt") ||
            package.Size is <= 0 or > MaximumInstallerBytes)
        {
            throw new InvalidDataException("The selected update cannot be downloaded safely.");
        }

        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var installerPath = Path.Combine(directory, installerName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var networkTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        networkTimeout.CancelAfter(NetworkTimeout);
        var expectedHash = await DownloadChecksumAsync(package.ChecksumDownload, installerName, networkTimeout.Token)
            .ConfigureAwait(false);
        if (expectedHash is null)
            throw new InvalidDataException("The published installer checksum is invalid.");

        // Each attempt owns its partial file, including when a download is cancelled.
        var partialPath = installerPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            networkTimeout.CancelAfter(NetworkTimeout);
            using var response = await OpenAssetAsync(package.InstallerDownload, networkTimeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != package.Size)
                throw new InvalidDataException("The installer has an unexpected size.");

            await using var source = await response.Content.ReadAsStreamAsync(networkTimeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var lastPercent = -1L;
                while (true)
                {
                    networkTimeout.CancelAfter(NetworkTimeout);
                    var count = await source.ReadAsync(buffer, networkTimeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > package.Size)
                        throw new InvalidDataException("The installer exceeded its declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    var percent = received * 100 / package.Size;
                    if (percent != lastPercent)
                    {
                        progress?.Report(new(received, package.Size));
                        lastPercent = percent;
                    }
                }
                if (received != package.Size)
                    throw new InvalidDataException("The installer download was incomplete.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");

            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, installerPath, true);
            RemoveOlderInstallers(directory, Version.Parse(package.Version));
            return new(package.Version, installerPath, expectedHash);
        }
        finally { DeleteIfPresent(partialPath); }
    }

    private async Task<string?> DownloadChecksumAsync(Uri uri, string installerName, CancellationToken cancellationToken)
    {
        using var response = await OpenAssetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumChecksumBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MaximumChecksumBytes + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (count > MaximumChecksumBytes)
            return null;
        foreach (var line in Encoding.ASCII.GetString(bytes, 0, count).Split('\n'))
        {
            var match = ChecksumLine().Match(line.TrimEnd('\r'));
            if (match.Success && string.Equals(match.Groups[2].Value, installerName, StringComparison.Ordinal))
                return match.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    // The caller's HTTP handler must have automatic redirection disabled.
    private async Task<HttpResponseMessage> OpenAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    response.EnsureSuccessStatusCode();

                if (redirects >= 3 || response.Headers.Location is not { } target ||
                    !UpdateChecker.IsDefaultHttps(target) ||
                    target.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub returned an unexpected update download address.");
                uri = target;
            }
        }
    }

    private static void RemoveOlderInstallers(string directory, Version current)
    {
        const string prefix = "HASSConnect-";
        const string suffix = "-Setup-x64.exe";
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*" + suffix))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    ReleaseVersion.TryParse(name[prefix.Length..^suffix.Length], out var version) && version < current)
                    DeleteIfPresent(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteIfPresent(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"\A([0-9a-fA-F]{64})\s+\*?([^\\/]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();
}
