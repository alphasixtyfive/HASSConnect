using System.Net;

namespace HassConnect.HomeAssistant;

public enum NotificationConnectionState { Connecting, Connected, Reconnecting, SignInRequired, DeviceRemoved }

/// <summary>Owns both registration and subscription recovery for one connection.</summary>
public static class NotificationReceiver
{
    public static Task RunAsync(Func<CancellationToken, Task> configure,
        Func<Action, CancellationToken, Task> receive, Action<NotificationConnectionState> changed,
        Action<Exception> failed, CancellationToken ct) =>
        RunAsync(configure, receive, changed, failed, Task.Delay, ct);

    internal static async Task RunAsync(Func<CancellationToken, Task> configure,
        Func<Action, CancellationToken, Task> receive, Action<NotificationConnectionState> changed,
        Action<Exception> failed, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                changed(failures == 0 ? NotificationConnectionState.Connecting : NotificationConnectionState.Reconnecting);
                await configure(ct);
                await receive(() => { failures = 0; changed(NotificationConnectionState.Connected); }, ct);
                ct.ThrowIfCancellationRequested();
                throw new IOException("The notification connection ended.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                changed(NotificationConnectionState.SignInRequired);
                return;
            }
            catch (RegistrationRemovedException)
            {
                changed(NotificationConnectionState.DeviceRemoved);
                return;
            }
            catch (Exception ex)
            {
                failed(ex);
                changed(NotificationConnectionState.Reconnecting);
            }
            failures = Math.Min(failures + 1, 4);
            try { await delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << failures))), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
