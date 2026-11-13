using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HassConnect.App.Services;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HassConnect-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath(string name) => Path.Combine(_directory, name);

    [Fact]
    public void NewIdentityIsStableAndNormalLoadDoesNotRewriteSettings()
    {
        var settings = new SettingsStore(_directory).LoadSettings();
        var written = File.GetLastWriteTimeUtc(FilePath("settings.json"));
        Assert.Equal(settings.DeviceId, new SettingsStore(_directory).LoadSettings().DeviceId);
        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath("settings.json")));
    }

    [Fact]
    public void CorruptSettingsRecoverWithoutEnablingOldCommands()
    {
        var store = new SettingsStore(_directory);
        var initial = store.LoadSettings() with { PcControlEnabled = true, EnabledSensors = ["cpu_usage"] };
        store.Save(initial);
        store.Save(initial with { PcControlEnabled = false });
        File.WriteAllText(FilePath("settings.json"), "{");
        var recoveredStore = new SettingsStore(_directory);
        var recovered = recoveredStore.LoadSettings();
        Assert.Equal(initial.DeviceId, recovered.DeviceId);
        Assert.Contains("cpu_usage", recovered.EnabledSensors);
        Assert.False(recovered.PcControlEnabled);
        Assert.Empty(recovered.EnabledPcCommands);
        Assert.NotNull(recoveredStore.RecoveryMessage);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.unreadable-*"));
        Assert.Equal(initial.DeviceId, JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(FilePath("settings.json.bak")))!.DeviceId);
    }

    [Fact]
    public void NewerSchemaCannotBeReplacedOrRecoveredFromOlderBackup()
    {
        var store = new SettingsStore(_directory);
        var settings = store.LoadSettings();
        store.Save(settings);
        var newer = JsonSerializer.Serialize(settings with { SchemaVersion = 2 });
        File.WriteAllText(FilePath("settings.json"), newer);
        Assert.Throws<UnsupportedSettingsVersionException>(() => new SettingsStore(_directory).LoadSettings());
        Assert.Throws<UnsupportedSettingsVersionException>(() => store.Save(settings));
        Assert.Equal(newer, File.ReadAllText(FilePath("settings.json")));
    }

    [Fact]
    public void CorruptCredentialsPreservePreferencesAndRequireSignIn()
    {
        var store = new SettingsStore(_directory);
        var settings = store.LoadSettings() with { EnabledSensors = ["cpu_usage"] };
        store.Save(settings);
        File.WriteAllText(FilePath("connection.dat"), "broken");
        var reopened = new SettingsStore(_directory);
        Assert.Contains("cpu_usage", reopened.LoadSettings().EnabledSensors);
        Assert.Null(reopened.LoadCredentials());
        Assert.NotNull(reopened.RecoveryMessage);
        Assert.Equal("broken", File.ReadAllText(FilePath("connection.dat")));
    }

    [Fact]
    public void InterruptedPairingIsCompletedFromEncryptedJournal()
    {
        var store = new SettingsStore(_directory);
        var settings = store.LoadSettings() with { ServerUrl = "https://ha.example/" };
        // Deny replacement by placing a directory at the credential file path.
        Directory.CreateDirectory(FilePath("connection.dat"));
        var error = Assert.Throws<InvalidOperationException>(() => store.SaveConnection(settings, new Credentials("test-token", "test-webhook")));
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        Assert.True(File.Exists(FilePath("pairing.pending")));
        Assert.DoesNotContain("test-token", Encoding.UTF8.GetString(File.ReadAllBytes(FilePath("pairing.pending"))));
        Directory.Delete(FilePath("connection.dat"));
        var reopened = new SettingsStore(_directory);
        Assert.Equal(settings.ServerUrl, reopened.LoadSettings().ServerUrl);
        Assert.Equal("test-webhook", reopened.LoadCredentials()!.WebhookId);
        Assert.False(File.Exists(FilePath("pairing.pending")));
    }

    [Fact]
    public void ResetKeepsChoicesAndRemovesCredentials()
    {
        var store = new SettingsStore(_directory);
        var settings = store.LoadSettings() with { EnabledSensors = ["cpu_usage"], ServerUrl = "https://ha.example/" };
        store.SaveConnection(settings, new Credentials("test-token", "test-webhook"));
        store.SaveConnection(settings with { ServerUrl = "" }, null);
        var reopened = new SettingsStore(_directory);
        Assert.Null(reopened.LoadCredentials());
        Assert.Contains("cpu_usage", reopened.LoadSettings().EnabledSensors);
        Assert.Equal(settings.DeviceId, reopened.LoadSettings().DeviceId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
