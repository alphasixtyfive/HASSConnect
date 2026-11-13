using System.Net;
using System.Reflection;
using System.Text.Json;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class NetworkRegressionTests
{
    [Fact]
    public async Task ReceiptIsConfirmedBeforeSlowDeliveryCompletes()
    {
        using var payload = JsonDocument.Parse("""{"message":"Review","hass_confirm_id":"review"}""");
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = false;
        var processing = NotificationChannel.ProcessEventAsync(payload.RootElement,
            (_, _) => releaseDelivery.Task, _ => { },
            _ => { confirmed = true; return Task.CompletedTask; }, CancellationToken.None);
        var confirmedWhileDeliveryPending = confirmed;
        releaseDelivery.SetResult();
        await processing;
        Assert.True(confirmedWhileDeliveryPending, "Receipt acknowledgement waits for delivery.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntityBodyReadHonorsRequestTimeout(bool statesOnly)
    {
        using var stop = new CancellationTokenSource();
        var stream = new StalledStream();
        using var handler = new StalledHandler(stream);
        using var client = new HaClient(new Uri("https://review.invalid/"), "review-only", handler);
        var http = (HttpClient)typeof(HaClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        http.Timeout = TimeSpan.FromMilliseconds(100);
        Task reading = statesOnly ? client.GetQuickActionStatesAsync(["light.desk"], stop.Token)
            : client.GetQuickActionEntitiesAsync(stop.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var finished = await Task.WhenAny(reading, Task.Delay(500)) == reading;
        await stop.CancelAsync();
        try { await reading; } catch (OperationCanceledException) { }
        Assert.True(finished, "Entity body remains blocked beyond HttpClient.Timeout.");
    }

    private sealed class StalledHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
