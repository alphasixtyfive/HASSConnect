using System.Security.Cryptography;
using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal sealed class SettingsStore
{
    private readonly string _directory = UserDataDirectory.Path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Settings LoadSettings()
    {
        var path = Path.Combine(_directory, "settings.json");
        if (!File.Exists(path)) return new Settings();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Settings are empty.");
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Settings were saved by a newer version. Update HASS Connect to open them.");
        if (settings.EnabledPcCommands is null || settings.EnabledSensors is null || settings.CustomCommands is null || settings.QuickActions is null)
            throw new InvalidDataException("Settings contain an empty collection.");
        return settings with
        {
            EnabledPcCommands = new(settings.EnabledPcCommands, StringComparer.Ordinal),
            EnabledSensors = new(settings.EnabledSensors, StringComparer.Ordinal),
            CustomCommands = CustomCommandPolicy.ValidateCollection(settings.CustomCommands).ToArray(),
            QuickActions = QuickActionPolicy.ValidateCollection(settings.QuickActions).ToArray(),
            QuickAccessShortcut = QuickAccessShortcutPolicy.Validate(settings.QuickAccessShortcut)
        };
    }

    public Credentials? LoadCredentials()
    {
        var path = Path.Combine(_directory, "connection.dat");
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Credentials>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(Settings settings)
    {
        QuickAccessShortcutPolicy.Validate(settings.QuickAccessShortcut);
        WriteAtomic("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json));
    }

    public void SaveCredentials(Credentials credentials)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try { WriteAtomic("connection.dat", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void WriteAtomic(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
