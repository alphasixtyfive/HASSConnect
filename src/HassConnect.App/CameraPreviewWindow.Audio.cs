using HassConnect.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App;

public sealed partial class CameraPreviewWindow
{
    private Task _audioTask = Task.CompletedTask;
    private CancellationTokenSource? _audioStop;
    private volatile bool _soundEnabled = true;
    private MicrophoneState _microphone;
    private bool _useVideoAudio;

    private enum MicrophoneState { Muted, Connecting, On, Unavailable }

    private bool MicrophoneInUse => _microphone is MicrophoneState.Connecting or MicrophoneState.On;
    private bool CanToggleMicrophone => _isLive || MicrophoneInUse;

    private void StartCameraAudio()
    {
        if (_soundEnabled) ChangeCameraAudio(microphone: false);
    }

    private void Sound_Click(object sender, RoutedEventArgs args)
    {
        _soundEnabled = !_soundEnabled;
        // A speaker toggle can leave an open microphone connected.
        if (_hasVideo && !MicrophoneInUse) ChangeCameraAudio(microphone: false);
        else UpdateAudioControls();
    }

    private void Microphone_Click(object sender, RoutedEventArgs args)
    {
        if (CanToggleMicrophone) ChangeCameraAudio(microphone: !MicrophoneInUse);
    }

    private void ChangeCameraAudio(bool microphone)
    {
        CancelCameraAudio();
        if ((_soundEnabled || microphone) && _request is { } request)
        {
            if (microphone) _microphone = MicrophoneState.Connecting;
            var stop = _audioStop = new();
            _audioTask = RunCameraAudioAsync(_audioTask, request.EntityId, microphone, stop);
        }
        UpdateAudioControls();
    }

    private async Task RunCameraAudioAsync(Task previous, string camera, bool microphone, CancellationTokenSource stop)
    {
        using (stop)
        {
            try
            {
                // Release the old audio devices and connection before starting the next mode.
                await previous;
                stop.Token.ThrowIfCancellationRequested();
                await _session.OpenCameraAudioAsync(camera, microphone, () => _soundEnabled, () => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_audioStop, stop)) return;
                    _microphone = microphone ? MicrophoneState.On : MicrophoneState.Muted;
                    UpdateAudioControls();
                }), stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AppLog.Write("Camera audio", ex);
                if (ReferenceEquals(_audioStop, stop))
                {
                    _useVideoAudio = true;
                    _microphone = microphone ? MicrophoneState.Unavailable : MicrophoneState.Muted;
                }
            }
            finally
            {
                if (ReferenceEquals(_audioStop, stop))
                {
                    _audioStop = null;
                    if (MicrophoneInUse) _microphone = MicrophoneState.Muted;
                    UpdateAudioControls();
                }
            }
        }
    }

    private void CancelCameraAudio()
    {
        var stop = _audioStop;
        _audioStop = null;
        stop?.Cancel();
        _microphone = MicrophoneState.Muted;
        _useVideoAudio = false;
    }

    private void UpdateAudioControls()
    {
        SoundButton.Visibility = MicrophoneButton.Visibility = _attempt is null ? Visibility.Collapsed : Visibility.Visible;
        MicrophoneButton.IsEnabled = CanToggleMicrophone;
        SoundButton.IsChecked = _soundEnabled;
        SoundIcon.Glyph = _soundEnabled ? "\uE767" : "\uE74F";
        ToolTipService.SetToolTip(SoundButton, _soundEnabled ? "Mute camera sound" : "Turn on camera sound");
        MicrophoneButton.IsChecked = MicrophoneInUse;
        MicrophoneIcon.Glyph = _microphone switch
        {
            MicrophoneState.On => "\uE720",
            MicrophoneState.Connecting => "\uEC55",
            _ => "\uEC54"
        };
        ToolTipService.SetToolTip(MicrophoneButton, _microphone switch
        {
            MicrophoneState.On => "Mute microphone",
            MicrophoneState.Connecting => "Cancel microphone connection",
            _ => "Unmute microphone"
        });
        if (_playback is { } playback) playback.Player.IsMuted = !(_soundEnabled && _useVideoAudio);
        if (_isLive) UpdateStatusText(_microphone == MicrophoneState.Unavailable ? "Live · Mic unavailable" : "Live");
    }
}
