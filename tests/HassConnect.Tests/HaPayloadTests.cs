using System.Net;
using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class HaPayloadTests
{
    [Fact]
    public async Task OptionalMetadataIsOmittedInsteadOfSendingNull()
    {
        using var handler = new PayloadHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "test", handler);
        var sensor = new SensorDefinition("session_locked", "Session locked", "", "binary_sensor", "mdi:lock");

        await client.RegisterSensorAsync("test", sensor, true, true, CancellationToken.None);

        var payload = handler.Payload.GetProperty("data");
        Assert.Equal("binary_sensor", payload.GetProperty("type").GetString());
        Assert.True(payload.GetProperty("state").GetBoolean());
        Assert.False(payload.TryGetProperty("device_class", out _));
        Assert.False(payload.TryGetProperty("unit_of_measurement", out _));
    }

    [Fact]
    public async Task UpdatingBinarySensorKeepsBooleanStateAndDomain()
    {
        using var handler = new PayloadHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "test", handler);

        await client.UpdateSensorsAsync("test", [new SensorReading("session_locked", false)], CancellationToken.None);

        var payload = handler.Payload.GetProperty("data")[0];
        Assert.Equal("binary_sensor", payload.GetProperty("type").GetString());
        Assert.False(payload.GetProperty("state").GetBoolean());
        Assert.Equal("mdi:lock-outline", payload.GetProperty("icon").GetString());
    }

    [Fact]
    public async Task UnavailableBinarySensorRegistersWithExplicitNullState()
    {
        using var handler = new PayloadHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "test", handler);
        var sensor = SensorDefinition.Available.Single(sensor => sensor.Id == "session_locked");

        await client.RegisterSensorAsync("test", sensor, null, true, CancellationToken.None);

        var payload = handler.Payload.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("state").ValueKind);
        Assert.False(payload.TryGetProperty("device_class", out _));
        Assert.False(payload.TryGetProperty("unit_of_measurement", out _));
    }

    [Fact]
    public async Task UnavailableBinarySensorUpdatesWithNullInsteadOfTruthyUnknownString()
    {
        using var handler = new PayloadHandler();
        using var client = new HaClient(new Uri("https://home.example/"), "test", handler);

        await client.UpdateSensorsAsync("test", [new SensorReading("session_locked", null)], CancellationToken.None);

        var payload = handler.Payload.GetProperty("data")[0];
        Assert.Equal("binary_sensor", payload.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("state").ValueKind);
    }

    private sealed class PayloadHandler : HttpMessageHandler
    {
        public JsonElement Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Payload = document.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
