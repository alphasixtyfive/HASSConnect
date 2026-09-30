using System.Diagnostics;
using System.Text.Json;
using HassConnect.Core;

namespace HassConnect.Tests;

public sealed class CustomCommandTests
{
    private static readonly string Executable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    [Fact]
    public void CreatesDirectShellFreeProcessWithLiteralArguments()
    {
        var command = CustomCommandPolicy.Create(
            "command_custom_open_notes", "Open notes", Executable,
            ["--literal", "argument with spaces", "& not-a-shell-operator"]);

        var start = CustomCommandPolicy.CreateStartInfo(command);

        Assert.Equal(Path.GetFullPath(Executable), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(Path.GetDirectoryName(Executable), start.WorkingDirectory);
        Assert.Equal(command.Arguments, start.ArgumentList.ToArray());
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    public void StartsSelectedBatchFileWithSpacesInItsPath(string extension)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"HASS Connect (script test) {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var script = Path.Combine(folder, "trusted action" + extension);
            var marker = Path.Combine(folder, "finished.txt");
            File.WriteAllText(script, $"@echo off\r\n>\"{marker}\" echo completed\r\n");
            var command = CustomCommandPolicy.Create("command_custom_batch", "Batch", script, []);
            var start = CustomCommandPolicy.CreateStartInfo(command);

            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.True(start.RedirectStandardInput);
            Assert.Equal(folder, start.WorkingDirectory);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("completed", File.ReadAllText(marker).Trim());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void PowerShellScriptUsesFileModeWithoutInlineCodeOrPolicyBypass()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"HASS Connect script test {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var script = Path.Combine(folder, "trusted action.ps1");
            var marker = Path.Combine(folder, "finished.txt");
            File.WriteAllText(script,
                $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'completed'\r\nexit 0\r\n");
            var command = CustomCommandPolicy.Create("command_custom_powershell", "PowerShell", script, []);
            var start = CustomCommandPolicy.CreateStartInfo(command);

            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"), start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.True(start.RedirectStandardInput);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-File", script], start.ArgumentList.ToArray());
            Assert.Empty(start.Arguments);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("completed", File.ReadAllText(marker).Trim());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("custom_open_notes")]
    [InlineData("command_custom_")]
    [InlineData("command_custom_Uppercase")]
    [InlineData("command_custom_1starts_with_number")]
    [InlineData("command_custom_contains-dash")]
    [InlineData("command_lock")]
    public void RejectsUnsafeOrReservedCommandIdentifiers(string id) =>
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create(id, "Open notes", Executable, []));

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:\\scripts\\task.psm1")]
    [InlineData("\\\\server\\share\\task.exe")]
    public void RejectsNonLocalOrUnsupportedPaths(string path) =>
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_test", "Test", path, [], requireExecutable: false));

    [Fact]
    public void ScriptsRejectLaunchOptionsAndBatchShellCharacters()
    {
        Assert.Throws<ArgumentException>(() => CustomCommandPolicy.Create(
            "command_custom_script", "Script", "C:\\Scripts\\action.ps1", ["-Extra"], requireExecutable: false));
        Assert.Throws<ArgumentException>(() => CustomCommandPolicy.Create(
            "command_custom_script", "Script", "C:\\Scripts\\a&b.cmd", [], requireExecutable: false));
    }

    [Fact]
    public void RejectsMissingExecutableWhenSavingOrLaunching()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_missing", "Missing", missing, []));
    }

    [Fact]
    public void RejectsControlCharactersAndArgumentFlooding()
    {
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_test", "Bad\nname", Executable, []));
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_test", "Test", Executable, ["bad\nargument"]));
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_test", "Test", Executable,
                Enumerable.Repeat("argument", CustomCommandPolicy.MaximumArguments + 1)));
    }

    [Theory]
    [InlineData("-applaunch\r1091500")]
    [InlineData("-applaunch\n1091500")]
    [InlineData("-applaunch\r\n1091500")]
    public void ParsesLaunchOptionsFromWindowsAndWinUiLineEndings(string text) =>
        Assert.Equal(["-applaunch", "1091500"], CustomCommandPolicy.ParseArguments(text));

    [Fact]
    public void CollectionRejectsDuplicateIdentifiersAndTooManyCommands()
    {
        var command = CustomCommandPolicy.Create(
            "command_custom_test", "Test", Executable, [], requireExecutable: false);
        Assert.Throws<InvalidDataException>(() => CustomCommandPolicy.ValidateCollection([command, command]));
        Assert.Throws<InvalidDataException>(() => CustomCommandPolicy.ValidateCollection(
            Enumerable.Range(0, CustomCommandPolicy.MaximumCommands + 1)
                .Select(index => command with { Id = $"command_custom_test_{index}" })));
    }

    [Fact]
    public void CollectionRejectsEmptyEntriesFromCorruptSettings() =>
        Assert.Throws<InvalidDataException>(() =>
            CustomCommandPolicy.ValidateCollection([null!]));

    [Fact]
    public void NotificationParsesOnlyWellFormedCustomCommandName()
    {
        using var valid = JsonDocument.Parse("""{"message":"command_custom_open_notes","data":{"arguments":"ignored"}}""");
        var command = NotificationMessage.Parse(valid.RootElement).Command;
        Assert.Equal(PcCommandKind.Custom, command?.Kind);
        Assert.Equal("command_custom_open_notes", command?.Id);

        using var invalid = JsonDocument.Parse("""{"message":"command_custom_Open_notes"}""");
        Assert.Null(NotificationMessage.Parse(invalid.RootElement).Command);
    }

    [Fact]
    public void SettingsRoundTripPreservesApprovedCommand()
    {
        var settings = new Settings
        {
            CustomCommands =
            [
                CustomCommandPolicy.Create(
                    "command_custom_open_notes", "Open notes", Executable, ["notes.txt"],
                    requireExecutable: false)
            ]
        };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        var command = Assert.Single(restored.CustomCommands);
        Assert.Equal("command_custom_open_notes", command.Id);
        Assert.Equal("Open notes", command.Name);
        Assert.Equal(["notes.txt"], command.Arguments);
    }
}
