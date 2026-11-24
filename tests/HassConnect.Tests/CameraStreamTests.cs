using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class CameraStreamTests
{
    private static readonly Uri Server = new("https://ha.example:8123/");

    [Fact]
    public async Task CameraTilesUseTheLocalViewerInsteadOfCallingAService()
    {
        var action = QuickActionPolicy.Create("camera.front_door", "Front door", null, QuickActionPolicy.ViewCamera);
        Assert.Equal("mdi:camera", action.Icon);
        Assert.Equal([QuickActionPolicy.ViewCamera], QuickActionPolicy.AllowedOperations(action.EntityId));
        using var client = new HaClient(Server, "test-token");

        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeQuickActionAsync(action, CancellationToken.None));
    }

    [Fact]
    public void ResolvesHomeAssistantHlsPathOnTheRegisteredServer()
    {
        var stream = HaClient.ResolveCameraStream(Server, "/api/hls/Sample-Token_123/master_playlist.m3u8");

        Assert.Equal("https://ha.example:8123/api/hls/Sample-Token_123/master_playlist.m3u8", stream.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://other.example/api/hls/Sample-Token_123/master_playlist.m3u8")]
    [InlineData("//other.example/api/hls/Sample-Token_123/master_playlist.m3u8")]
    [InlineData("/api/hls/Sample-Token_123/../master_playlist.m3u8")]
    [InlineData("/api/hls/Sample-Token_123/%2e%2e/master_playlist.m3u8")]
    [InlineData("/api/hls/Sample-Token_123/master_playlist.m3u8?redirect=1")]
    public void RejectsUrlsOutsideTheHomeAssistantHlsEndpoint(string? path) =>
        Assert.Throws<InvalidDataException>(() => HaClient.ResolveCameraStream(Server, path));

    [Theory]
    [InlineData("")]
    [InlineData("light.front_door")]
    [InlineData("camera.front_door/other")]
    public async Task RejectsInvalidCameraEntitiesBeforeConnecting(string entity)
    {
        using var client = new HaClient(Server, "test-token");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.GetCameraStreamAsync(entity, CancellationToken.None));
        Assert.Equal("entityId", error.ParamName);
    }
}
