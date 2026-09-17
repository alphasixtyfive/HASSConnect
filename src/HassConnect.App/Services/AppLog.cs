namespace HassConnect.App.Services;

internal static class AppLog
{
    public static string DataDirectory => UserDataDirectory.Path;
    private static readonly object Gate = new();

    public static void Write(string operation, string detail)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation}: {detail}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
