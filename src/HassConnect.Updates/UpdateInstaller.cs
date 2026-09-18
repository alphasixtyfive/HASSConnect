using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HassConnect.Core;

namespace HassConnect.Updates;

public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);
public sealed record DownloadedUpdate(string Version, string InstallerPath, string Sha256);

/// <summary>Downloads a release installer and verifies it against the published SHA-256 manifest.</summary>
public sealed partial class UpdateInstaller(HttpClient client)
{
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;

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
        var partialPath = installerPath + ".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var expectedHash = await DownloadChecksumAsync(
            package.ChecksumDownload, installerName, timeout.Token).ConfigureAwait(false);
        if (expectedHash is null)
        {
            DeleteIfPresent(partialPath);
            throw new InvalidDataException("The published installer checksum is invalid.");
        }

        try
        {
            using var response = await client.GetAsync(
                package.InstallerDownload, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Size;
            if (total is <= 0 or > MaximumInstallerBytes)
                throw new InvalidDataException("The installer has an unexpected size.");

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var destination = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > MaximumInstallerBytes || received > total)
                        throw new InvalidDataException("The installer exceeded its declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new(received, total));
                }
                if (received != total)
                    throw new InvalidDataException("The installer download was incomplete.");
            }

            string actualHash;
            await using (var downloaded = new FileStream(
                partialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(downloaded, timeout.Token).ConfigureAwait(false));
            }
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");

            File.Move(partialPath, installerPath, true);
            RemoveOlderInstallers(directory, installerPath);
            return new(package.Version, installerPath, expectedHash);
        }
        catch
        {
            DeleteIfPresent(partialPath);
            throw;
        }
    }

    private async Task<string?> DownloadChecksumAsync(Uri uri, string installerName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 64 * 1024)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 64 * 1024)
            return null;
        foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n'))
        {
            var match = ChecksumLine().Match(line.TrimEnd('\r'));
            if (match.Success && string.Equals(match.Groups[2].Value, installerName, StringComparison.Ordinal))
                return match.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    private static void RemoveOlderInstallers(string directory, string current)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "HASSConnect-*-Setup-x64.exe"))
            if (!path.Equals(current, StringComparison.OrdinalIgnoreCase)) DeleteIfPresent(path);
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
