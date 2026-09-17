namespace HassConnect.App.Services;

internal static class StorageSensors
{
    private const double BytesPerGigabyte = 1_000_000_000d;

    public static double Read(string id)
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)
            ?? throw new InvalidDataException("Windows did not report a system disk.");
        var disk = new DriveInfo(root);
        if (!disk.IsReady || disk.TotalSize <= 0)
            throw new InvalidDataException("The Windows system disk is not ready.");

        return id switch
        {
            "disk_usage" => Math.Round(100d * (disk.TotalSize - disk.AvailableFreeSpace) / disk.TotalSize, 1),
            "disk_free_space" => Math.Round(disk.AvailableFreeSpace / BytesPerGigabyte, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
    }
}
