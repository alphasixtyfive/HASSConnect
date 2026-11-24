using System.Buffers.Binary;
using System.Threading.Channels;
using HassConnect.HomeAssistant;
using NAudio.Codecs;
using NAudio.Wave;

namespace HassConnect.App.Services;

internal static class CameraAudio
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(Uri homeAssistant, string entityId, bool microphoneEnabled,
        Func<bool> speakerEnabled, Action connected, CancellationToken ct)
    {
        using var audio = await Go2RtcAudio.ConnectAsync(homeAssistant, entityId, microphoneEnabled, ct).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var format = new WaveFormat(8000, 16, 1);
        var speakerBuffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true
        };
        using var speaker = new WaveOutEvent { DesiredLatency = 100 };
        var speakerFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        speaker.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) speakerFailure.TrySetResult(e.Exception);
        };

        ct.ThrowIfCancellationRequested();
        speaker.Init(speakerBuffer);
        speaker.Play();
        if (!microphoneEnabled) connected();
        var receive = ReceiveAsync(audio, speakerBuffer, speakerEnabled, stop.Token);
        var send = microphoneEnabled ? SendMicrophoneAsync(audio, format, connected, stop.Token) : Task.Delay(Timeout.Infinite, stop.Token);
        try
        {
            var completed = await Task.WhenAny(receive, send, speakerFailure.Task).ConfigureAwait(false);
            if (completed == speakerFailure.Task) throw await speakerFailure.Task.ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new IOException("Camera audio disconnected.");
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
            catch when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task SendMicrophoneAsync(Go2RtcAudio audio, WaveFormat format, Action connected, CancellationToken ct)
    {
        using var microphone = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 20, NumberOfBuffers = 3 };
        var microphoneFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        microphone.DataAvailable += (_, e) =>
        {
            if (ct.IsCancellationRequested) return;
            var samples = new byte[e.BytesRecorded / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = MuLawEncoder.LinearToMuLawSample(BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i * 2)));
            if (samples.Length != 0) microphoneFrames.Writer.TryWrite(samples);
        };
        microphone.RecordingStopped += (_, e) => microphoneFrames.Writer.TryComplete(e.Exception);

        ct.ThrowIfCancellationRequested();
        microphone.StartRecording();
        connected();
        await foreach (var frame in microphoneFrames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await audio.SendAudioAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task ReceiveAsync(Go2RtcAudio audio, BufferedWaveProvider speaker,
        Func<bool> speakerEnabled, CancellationToken ct)
    {
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pcm = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            readDeadline.CancelAfter(ReceiveTimeout);
            ReadOnlyMemory<byte> bytes;
            try { bytes = await audio.ReadAudioAsync(readDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The camera stopped sending audio.");
            }
            if (!speakerEnabled())
            {
                speaker.ClearBuffer();
                continue;
            }
            for (var i = 0; i < bytes.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), MuLawDecoder.MuLawToLinearSample(bytes.Span[i]));
            speaker.AddSamples(pcm, 0, bytes.Length * 2);
        }
    }
}
