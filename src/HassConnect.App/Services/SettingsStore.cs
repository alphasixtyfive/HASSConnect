using System.Security.Cryptography;
using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal sealed class SettingsStore(string? directory = null)
{
    private readonly string _directory = directory ?? UserDataDirectory.Path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private bool _recoveryChecked;
    private bool _unreadablePairing;
    public string? RecoveryMessage { get; private set; }

    public Settings LoadSettings()
    {
        RecoverPairing();
        var path = Path.Combine(_directory, "settings.json");
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            var created = new Settings();
            Save(created); // Persist the device identity once, before registration.
            return created;
        }
        try { return ReadSettings(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException)
        {
            var recovered = ReadSettings(path + ".bak");
            // A backup may predate a command being disabled. Require fresh opt-in.
            recovered = recovered with
            {
                PcControlEnabled = false, EnabledPcCommands = [],
                CustomCommands = recovered.CustomCommands.Select(command => command with { Enabled = false }).ToArray()
            };
            Preserve(path);
            WriteAtomic("settings.json", JsonSerializer.SerializeToUtf8Bytes(recovered, Json));
            RecoveryMessage = "Settings recovered. Review PC controls before enabling them.";
            return recovered;
        }
    }

    public Credentials? LoadCredentials()
    {
        RecoverPairing();
        if (_unreadablePairing) return null;
        var path = Path.Combine(_directory, "connection.dat");
        if (!File.Exists(path)) return null;
        try { return ValidateCredentials(ReadProtected<Credentials>(path) ?? throw new InvalidDataException("Saved credentials are empty.")); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            RecoveryMessage = "Enter your access token to reconnect. Your settings are unchanged.";
            return null;
        }
    }

    public void Save(Settings settings)
    {
        if (File.Exists(Path.Combine(_directory, "pairing.pending")) && !_unreadablePairing)
            throw new InvalidOperationException("Restart HASS Connect to finish saving the connection.");
        RecoverPairing();
        SaveSettings(settings);
    }

    public void SaveConnection(Settings settings, Credentials? credentials)
    {
        RecoverPairing();
        Validate(settings);
        if (credentials is not null) ValidateCredentials(credentials);
        var pending = Path.Combine(_directory, "pairing.pending");
        if (_unreadablePairing) Preserve(pending);
        // Replay this encrypted journal if either subsequent replacement fails.
        WriteProtected("pairing.pending", new Pairing(settings, credentials));
        _unreadablePairing = false;
        _recoveryChecked = false;
        try { RecoverPairing(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The connection could not finish saving. Check folder access and disk space, then restart HASS Connect to recover it.", ex);
        }
    }

    private void RecoverPairing()
    {
        if (_recoveryChecked) return;
        var path = Path.Combine(_directory, "pairing.pending");
        if (!File.Exists(path)) { _recoveryChecked = true; return; }
        Pairing pending;
        try
        {
            pending = ReadProtected<Pairing>(path) ?? throw new InvalidDataException("Pairing is empty.");
            Validate(pending.Settings);
            if (pending.Credentials is not null) ValidateCredentials(pending.Credentials);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            _unreadablePairing = true;
            _recoveryChecked = true;
            RecoveryMessage = "Enter your access token to finish reconnecting.";
            return;
        }
        CheckExistingVersion();
        SaveSettings(pending.Settings);
        if (pending.Credentials is null) File.Delete(Path.Combine(_directory, "connection.dat"));
        else WriteProtected("connection.dat", pending.Credentials);
        File.Delete(path);
        _recoveryChecked = true;
    }

    private void SaveSettings(Settings settings)
    {
        settings = Validate(settings);
        CheckExistingVersion();
        var path = Path.Combine(_directory, "settings.json");
        if (File.Exists(path))
        {
            try
            {
                ReadSettings(path);
                WriteAtomic("settings.json.bak", File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { Preserve(path); }
        }
        WriteAtomic("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, Json));
    }

    private void CheckExistingVersion()
    {
        var path = Path.Combine(_directory, "settings.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(nameof(Settings.SchemaVersion), out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version != 1)
                throw new UnsupportedSettingsVersionException();
        }
        catch (JsonException) { }
    }

    private static Settings ReadSettings(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(nameof(Settings.SchemaVersion), out var schema) &&
            schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version != 1)
            throw new UnsupportedSettingsVersionException();
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(Settings.DeviceId), out var id) || id.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Settings have no device identity.");
        return Validate(document.RootElement.Deserialize<Settings>() ?? throw new InvalidDataException("Settings are empty."));
    }

    private static Settings Validate(Settings settings)
    {
        if (settings is null) throw new InvalidDataException("Settings are empty.");
        if (settings.SchemaVersion != 1) throw new UnsupportedSettingsVersionException();
        if (string.IsNullOrWhiteSpace(settings.DeviceId) || settings.EnabledPcCommands is null ||
            settings.EnabledSensors is null || settings.CustomCommands is null || settings.QuickActions is null ||
            settings.PendingSensorChanges is null)
            throw new InvalidDataException("Settings are incomplete.");
        if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            try { ServerAddress.Parse(settings.ServerUrl); }
            catch (ArgumentException ex) { throw new InvalidDataException("Saved server address is invalid.", ex); }
        }
        return settings with
        {
            EnabledPcCommands = new(settings.EnabledPcCommands, StringComparer.Ordinal),
            EnabledSensors = new(settings.EnabledSensors, StringComparer.Ordinal),
            PendingSensorChanges = new(settings.PendingSensorChanges, StringComparer.Ordinal),
            CustomCommands = CustomCommandPolicy.ValidateCollection(settings.CustomCommands).ToArray(),
            QuickActions = QuickActionPolicy.ValidateCollection(settings.QuickActions).ToArray(),
            QuickAccessShortcut = QuickAccessShortcutPolicy.Validate(settings.QuickAccessShortcut)
        };
    }

    private static Credentials ValidateCredentials(Credentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessToken) || credentials.AccessToken.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidDataException("Saved credentials are incomplete.");
        return credentials;
    }

    private static T? ReadProtected<T>(string path)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void WriteProtected<T>(string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { WriteAtomic(name, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void Preserve(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N"));
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

    private sealed record Pairing(Settings Settings, Credentials? Credentials);
}

internal sealed class UnsupportedSettingsVersionException() : Exception(
    "Settings were saved by a newer version. Update HASS Connect to open them.");
