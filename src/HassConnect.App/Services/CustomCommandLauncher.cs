using System.ComponentModel;
using System.Diagnostics;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal interface ICustomCommandLauncher
{
    void Execute(CustomCommandDefinition command);
}

internal sealed class CustomCommandLauncher : ICustomCommandLauncher
{
    private static readonly TimeSpan PerCommandInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);
    private const int GlobalLimit = 10;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRuns = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentRuns = new();

    public void Execute(CustomCommandDefinition command)
    {
        var start = CustomCommandPolicy.CreateStartInfo(command);
        Reserve(command.Id);
        using var process = Process.Start(start);
        if (process is null)
            throw new Win32Exception("Windows did not start the configured executable.");
    }

    private void Reserve(string id)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_recentRuns.TryPeek(out var oldest) && now - oldest >= GlobalWindow)
                _recentRuns.Dequeue();
            if (_lastRuns.TryGetValue(id, out var previous) && now - previous < PerCommandInterval)
                throw new InvalidOperationException("The custom command was triggered too quickly.");
            if (_recentRuns.Count >= GlobalLimit)
                throw new InvalidOperationException("Too many custom commands were triggered in one minute.");
            _lastRuns[id] = now;
            _recentRuns.Enqueue(now);
        }
    }
}
