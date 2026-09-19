namespace HassConnect.App.Services;

using HassConnect.Core;

internal static class StorageSensors
{
    private const double BytesPerGigabyte = 1_000_000_000d;

    public static double Read(string id)
    {
        if (DriveSensor.TryParse(id, out var letter, out var freeSpace))
            return ReadDrive(letter, freeSpace);

        var disk = GetReadyFixedDrive(SystemDriveLetter);

        return id switch
        {
            "disk_usage" => Math.Round(100d * (disk.TotalSize - disk.AvailableFreeSpace) / disk.TotalSize, 1),
            "disk_free_space" => Math.Round(disk.AvailableFreeSpace / BytesPerGigabyte, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
    }

    public static IReadOnlyList<char> AvailableDriveLetters()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed)
                .Select(drive => char.ToLowerInvariant(drive.Name[0]))
                .Where(letter => letter != SystemDriveLetter)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static char SystemDriveLetter
    {
        get
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root) || !char.IsAsciiLetter(root[0]))
                throw new InvalidDataException("Windows did not report a system disk.");
            return char.ToLowerInvariant(root[0]);
        }
    }

    public static string DisplayName(char letter, bool system)
    {
        var upper = char.ToUpperInvariant(letter);
        try
        {
            var drive = new DriveInfo($"{upper}:\\");
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                return $"{drive.VolumeLabel.Trim()} ({upper}:)";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return system ? $"Windows ({upper}:)" : $"Drive {upper}:";
    }

    private static double ReadDrive(char letter, bool freeSpace)
    {
        var disk = GetReadyFixedDrive(letter);
        return freeSpace
            ? Math.Round(disk.AvailableFreeSpace / BytesPerGigabyte, 1)
            : Math.Round(100d * (disk.TotalSize - disk.AvailableFreeSpace) / disk.TotalSize, 1);
    }

    private static DriveInfo GetReadyFixedDrive(char letter)
    {
        try
        {
            var disk = new DriveInfo($"{char.ToUpperInvariant(letter)}:\\");
            if (disk.DriveType != DriveType.Fixed || !disk.IsReady || disk.TotalSize <= 0)
                throw new InvalidDataException($"Drive {char.ToUpperInvariant(letter)}: is unavailable.");
            return disk;
        }
        catch (IOException ex) { throw new InvalidDataException("Windows could not read the selected drive.", ex); }
        catch (UnauthorizedAccessException ex) { throw new InvalidDataException("Windows denied access to the selected drive.", ex); }
    }

}
