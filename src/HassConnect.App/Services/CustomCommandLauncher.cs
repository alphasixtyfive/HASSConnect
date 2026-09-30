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
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(5);
    private const int GlobalLimit = 10;
    private const int ConcurrentScriptLimit = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRuns = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentRuns = new();
    private readonly Dictionary<string, Process> _runningScripts = new(StringComparer.Ordinal);

    public void Execute(CustomCommandDefinition command)
    {
        var start = CustomCommandPolicy.CreateStartInfo(command);
        var script = CustomCommandPolicy.IsScript(command);
        Process process;
        lock (_gate)
        {
            if (script && _runningScripts.ContainsKey(command.Id))
                throw new InvalidOperationException("This script is already running.");
            if (script && _runningScripts.Count >= ConcurrentScriptLimit)
                throw new InvalidOperationException("Too many scripts are running.");
            Reserve(command.Id);
            process = Process.Start(start) ??
                throw new Win32Exception("Windows did not start the configured program or script.");
            if (script) _runningScripts.Add(command.Id, process);
        }
        if (!script)
        {
            process.Dispose();
            return;
        }
        _ = MonitorScriptAsync(command.Id, process);
    }

    private async Task MonitorScriptAsync(string id, Process process)
    {
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(ScriptTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                AppLog.Write("Custom command", $"{id}: exited with code {process.ExitCode}");
            }
            catch (OperationCanceledException) when (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                AppLog.Write("Custom command", $"{id}: stopped after five minutes");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Custom command", $"{id}: {ex.GetType().Name}");
        }
        finally
        {
            lock (_gate) _runningScripts.Remove(id);
            process.Dispose();
        }
    }

    private void Reserve(string id)
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
