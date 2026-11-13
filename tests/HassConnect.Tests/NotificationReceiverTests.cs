using System.Net;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class NotificationReceiverTests
{
    [Fact]
    public async Task OfflineSetupIsRetriedBeforeSubscribing()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var states = new List<NotificationConnectionState>();
        await NotificationReceiver.RunAsync(_ => ++attempts == 1
                ? Task.FromException(new HttpRequestException()) : Task.CompletedTask,
            (connected, _) => { connected(); stop.Cancel(); return Task.CompletedTask; },
            states.Add, _ => { }, (_, _) => Task.CompletedTask, stop.Token);
        Assert.Equal(2, attempts);
        Assert.Contains(NotificationConnectionState.Reconnecting, states);
        Assert.Equal(NotificationConnectionState.Connected, states.Last());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedTokenStopsRetries(HttpStatusCode status)
    {
        var states = new List<NotificationConnectionState>();
        var retries = 0;
        await NotificationReceiver.RunAsync(_ => Task.CompletedTask,
            (_, _) => Task.FromException(new HttpRequestException("rejected", null, status)),
            states.Add, _ => { }, (_, _) => { retries++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, retries);
        Assert.Equal(NotificationConnectionState.SignInRequired, states.Last());
    }

    [Fact]
    public async Task CancellationStopsRetryDelay()
    {
        using var stop = new CancellationTokenSource();
        await NotificationReceiver.RunAsync(_ => Task.FromException(new IOException()),
            (_, _) => throw new InvalidOperationException("Must not subscribe"), _ => { }, _ => { },
            (_, ct) => { stop.Cancel(); return Task.Delay(Timeout.Infinite, ct); }, stop.Token);
    }
}
