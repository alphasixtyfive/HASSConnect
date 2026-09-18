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
    [InlineData("C:\\scripts\\task.cmd")]
    [InlineData("\\\\server\\share\\task.exe")]
    public void RejectsNonLocalOrNonExecutablePaths(string path) =>
        Assert.Throws<ArgumentException>(() =>
            CustomCommandPolicy.Create("command_custom_test", "Test", path, [], requireExecutable: false));

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
