using System.Diagnostics;
using System.Net;
using HassConnect.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;
using Windows.Storage;

namespace HassConnect.App;

public sealed partial class CameraPreviewWindow : Window
{
    private readonly SensorSession _session;
    private readonly DispatcherQueueTimer _playbackTimeout;
    private CameraOpenRequest? _request;
    private CameraPlayback? _playback;
    private CancellationTokenSource? _attempt;
    private CancellationTokenSource? _snapshotStop;
    private long _startedAt;
    private bool _hasVideo;
    private bool _isLive;
    private bool _retried;

    internal CameraPreviewWindow(SensorSession session, ElementTheme theme)
    {
        InitializeComponent();
        _session = session;
        _playbackTimeout = DispatcherQueue.CreateTimer();
        _playbackTimeout.IsRepeating = false;
        _playbackTimeout.Tick += (_, _) => RecoverPlayback("Connection timed out");

        SetTheme(theme);
        Surface.ActualThemeChanged += (_, _) => WindowTitleBar.Apply(this, Surface.ActualTheme);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        Surface.Loaded += Surface_Loaded;
        Closed += (_, _) =>
        {
            _request = null;
            _cameraActionDialog?.Hide();
            StopPlayback();
            ClearSnapshot();
        };
    }

    internal void SetTheme(ElementTheme theme)
    {
        Surface.RequestedTheme = theme;
        WindowTitleBar.Apply(this, Surface.ActualTheme);
    }

    private void Surface_Loaded(object sender, RoutedEventArgs args)
    {
        Surface.Loaded -= Surface_Loaded;
        WindowTitleBar.Apply(this, Surface.ActualTheme);
        UpdateMinimumSize();
        if (Surface.XamlRoot is not { } root) return;
        root.Changed += XamlRoot_Changed;
        Closed += (_, _) => root.Changed -= XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateMinimumSize();

    internal void OpenCamera(CameraOpenRequest request)
    {
        // Repeated alerts for the current camera keep its video and microphone state.
        if (_attempt is null || _request?.EntityId != request.EntityId)
        {
            _cameraActionDialog?.Hide();
            CameraActionFeedback.IsOpen = false;
            StopPlayback();
            ClearSnapshot();
            _request = request;
            Title = string.IsNullOrWhiteSpace(request.Title) ? request.EntityId : request.Title;
            _retried = false;
            _ = StartPlaybackAsync();
        }
        if (request.LoadSnapshot is { } loadSnapshot) _ = ShowSnapshotAsync(loadSnapshot);

        RefreshCameraActions();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = request.Automatic;
        if (request.Automatic) AppWindow.Show(activateWindow: false);
        else Activate();
    }

    private void Close_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void Retry_Click(object sender, RoutedEventArgs args)
    {
        if (_attempt is not null) return;
        _retried = false;
        _ = StartPlaybackAsync();
    }

    private async Task StartPlaybackAsync()
    {
        if (_request is not { } request) return;
        var attempt = _attempt = new();
        var token = attempt.Token;
        _startedAt = Stopwatch.GetTimestamp();
        SetStatus(_retried ? "Reconnecting…" : "Connecting…", busy: true);
        _playbackTimeout.Interval = TimeSpan.FromSeconds(_retried ? 12 : 15);
        _playbackTimeout.Start();

        try
        {
            var uri = await _session.GetCameraStreamAsync(request.EntityId, token);
            token.ThrowIfCancellationRequested();
            var playback = await CameraPlayback.OpenAsync(uri, lowLatency: !_retried, token);
            if (token.IsCancellationRequested)
            {
                playback?.Dispose();
                return;
            }

            if (playback is null)
            {
                RecoverPlayback("Can’t open the stream");
                return;
            }

            _playback = playback;
            var player = playback.Player;
            player.PlaybackSession.PlaybackStateChanged += PlaybackSession_Changed;
            player.PlaybackSession.NaturalVideoSizeChanged += PlaybackSession_Changed;
            player.MediaFailed += Media_Failed;
            player.MediaEnded += Media_Ended;
            Video.SetMediaPlayer(player);
            playback.Start();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_attempt, attempt)) return;
            AppLog.Write("Camera popup", ex);
            if (ex is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout })
            {
                RecoverPlayback("Can’t connect to camera");
                return;
            }
            EndPlayback(ex switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "Camera sign-in required",
                InvalidDataException => "Live stream unavailable",
                _ => "Can’t connect to camera"
            });
        }
    }

    private void PlaybackSession_Changed(MediaPlaybackSession session, object args)
    {
        var observedAt = Stopwatch.GetTimestamp();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_playback?.Player.PlaybackSession != session) return;
            UpdateFraming();
            if (session.PlaybackState == MediaPlaybackState.Playing && session.NaturalVideoWidth > 0)
            {
                _playbackTimeout.Stop();
                var firstFrame = !_hasVideo;
                _hasVideo = true;
                if (!_isLive) SetStatus("Live", live: true);
                if (firstFrame)
                {
                    AppLog.Write("Camera playback startup", $"{_request?.EntityId}: {Stopwatch.GetElapsedTime(_startedAt, observedAt).TotalMilliseconds:F0} ms");
                    StartCameraAudio();
                }
            }
            else if (session.PlaybackState == MediaPlaybackState.Buffering && _hasVideo && !_playbackTimeout.IsRunning)
            {
                _playbackTimeout.Interval = TimeSpan.FromSeconds(10);
                _playbackTimeout.Start();
                SetStatus("Buffering…", busy: true);
            }
        });
    }

    private void Media_Failed(MediaPlayer player, MediaPlayerFailedEventArgs args)
    {
        var error = args.Error;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_playback?.Player != player) return;
            AppLog.Write("Camera playback", error.ToString());
            RecoverPlayback("Live stream unavailable");
        });
    }

    private void Media_Ended(MediaPlayer player, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_playback?.Player == player) RecoverPlayback("Stream ended");
    });

    private void RecoverPlayback(string message)
    {
        if (_attempt is null) return;
        if (_retried)
        {
            EndPlayback(message);
            return;
        }

        AppLog.Write("Camera playback", $"{_request?.EntityId}: {message}; retrying");
        _retried = true;
        StopPlayback();
        _ = StartPlaybackAsync();
    }

    private void EndPlayback(string message)
    {
        StopPlayback();
        SetStatus(message);
    }

    private async Task ShowSnapshotAsync(Func<CancellationToken, Task<string?>> loadSnapshot)
    {
        CancelSnapshotLoad();
        using var stop = _snapshotStop = new();
        try
        {
            var path = await loadSnapshot(stop.Token);
            if (path is null || stop.IsCancellationRequested) return;
            var file = await StorageFile.GetFileFromPathAsync(new Uri(path).LocalPath).AsTask(stop.Token);
            using var stream = await file.OpenReadAsync().AsTask(stop.Token);
            var image = new BitmapImage { DecodePixelWidth = 800 };
            await image.SetSourceAsync(stream).AsTask(stop.Token);
            if (stop.IsCancellationRequested) return;
            Snapshot.Source = image;
            UpdateStage();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Write("Camera snapshot", ex); }
        finally { if (ReferenceEquals(_snapshotStop, stop)) _snapshotStop = null; }
    }

    private void ClearSnapshot()
    {
        CancelSnapshotLoad();
        Snapshot.Source = null;
    }

    private void CancelSnapshotLoad()
    {
        _snapshotStop?.Cancel();
        _snapshotStop = null;
    }

    private void UpdateMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var scale = Surface.XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Ceiling(460 * scale);
        var height = (int)Math.Ceiling(330 * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void SetStatus(string text, bool busy = false, bool live = false)
    {
        _isLive = live;
        if (!live) UpdateStatusText(text);
        Loading.IsActive = busy;
        Loading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LiveIndicator.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _attempt is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateStage();
        UpdateAudioControls();
        UpdateCameraActionAvailability();
    }

    private void UpdateStatusText(string text)
    {
        if (StreamStatus.Text == text) return;
        StreamStatus.Text = text;
        ToolTipService.SetToolTip(StreamStatus, text);
    }

    private void UpdateStage()
    {
        var hasSnapshot = !_hasVideo && Snapshot.Source is not null;
        Snapshot.Visibility = SnapshotBadge.Visibility = hasSnapshot ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreview.Visibility = !_hasVideo && !hasSnapshot ? Visibility.Visible : Visibility.Collapsed;
        UpdateFraming();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateFraming();

    private void UpdateFraming()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var viewportRatio = Viewport.ActualWidth / Viewport.ActualHeight;
        Stretch Fit(double width, double height)
        {
            if (width <= 0 || height <= 0) return Stretch.Uniform;
            var mediaRatio = width / height;
            // Fill small gaps and preserve the whole picture when proportions differ substantially.
            return Math.Min(viewportRatio, mediaRatio) / Math.Max(viewportRatio, mediaRatio) >= 0.94
                ? Stretch.UniformToFill : Stretch.Uniform;
        }
        var video = _playback?.Player.PlaybackSession;
        Video.Stretch = Fit(video?.NaturalVideoWidth ?? 0, video?.NaturalVideoHeight ?? 0);
        Snapshot.Stretch = Snapshot.Source is BitmapImage image ? Fit(image.PixelWidth, image.PixelHeight) : Stretch.Uniform;
    }

    private void StopPlayback()
    {
        CancelCameraAudio();
        _playbackTimeout.Stop();
        _hasVideo = false;
        _isLive = false;
        var attempt = _attempt;
        _attempt = null;
        attempt?.Cancel();
        attempt?.Dispose();
        using var playback = _playback;
        _playback = null;
        if (playback is null) return;
        var player = playback.Player;
        player.PlaybackSession.PlaybackStateChanged -= PlaybackSession_Changed;
        player.PlaybackSession.NaturalVideoSizeChanged -= PlaybackSession_Changed;
        player.MediaFailed -= Media_Failed;
        player.MediaEnded -= Media_Ended;
        try { player.Source = null; }
        finally { Video.SetMediaPlayer(null); }
    }
}
