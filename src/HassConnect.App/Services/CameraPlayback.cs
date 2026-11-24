using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Web.Http.Filters;
using MediaHttpClient = Windows.Web.Http.HttpClient;

namespace HassConnect.App.Services;

internal sealed class CameraPlayback(MediaHttpClient http, MediaSource source, MediaPlayer player) : IDisposable
{
    public MediaPlayer Player { get; } = player;

    public static async Task<CameraPlayback?> OpenAsync(Uri uri, bool lowLatency, CancellationToken ct)
    {
        var filter = new HttpBaseProtocolFilter { AllowAutoRedirect = false };
        // Home Assistant reuses the same playlist URL as live segments advance.
        filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
        filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
        var http = new MediaHttpClient(filter);
        MediaSource? source = null;
        MediaPlayer? player = null;
        try
        {
            var result = await AdaptiveMediaSource.CreateFromUriAsync(uri, http).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                AppLog.Write("Camera manifest", result.Status.ToString());
                http.Dispose();
                return null;
            }

            source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
            player = new MediaPlayer { IsMuted = true, RealTimePlayback = lowLatency };
            player.CommandManager.IsEnabled = false;
            return new(http, source, player);
        }
        catch
        {
            player?.Dispose();
            source?.Dispose();
            http.Dispose();
            throw;
        }
    }

    public void Start()
    {
        Player.Source = source;
        Player.Play();
    }

    public void Dispose()
    {
        using (http)
        using (source)
            Player.Dispose();
    }
}
