namespace HassConnect.App.Services;

internal sealed class NotificationImageCache
{
    private const int RetainedImages = 20;
    private readonly string _directory = Path.Combine(UserDataDirectory.Path, "NotificationImages");

    public async Task<string> StoreAsync(byte[] image, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var extension = image[0] == 137 ? ".png" : image[0] == 255 ? ".jpg" : ".gif";
        var name = Guid.NewGuid().ToString("N") + extension;
        var path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, image, cancellationToken);
        Prune(path);
        return new Uri(path).AbsoluteUri;
    }

    private void Prune(string currentImage)
    {
        try
        {
            foreach (var old in new DirectoryInfo(_directory).EnumerateFiles()
                .Where(file => file.FullName != currentImage)
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(RetainedImages - 1))
                old.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache maintenance must not prevent a newly downloaded image from displaying.
            AppLog.Write("Notification cache cleanup", exception.GetType().Name);
        }
    }
}
