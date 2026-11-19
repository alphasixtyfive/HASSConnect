using System.Net;
using System.Text;
using System.Text.Json;
using HassConnect.Core;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class QuickActionTests
{
    [Theory]
    [InlineData("light.desk", "toggle")]
    [InlineData("light.desk", "brightness")]
    [InlineData("media_player.speakers", "volume")]
    [InlineData("fan.office", "turn_on")]
    [InlineData("input_boolean.away", "turn_off")]
    [InlineData("script.good_night", "run")]
    [InlineData("scene.evening", "run")]
    [InlineData("button.restart", "run")]
    [InlineData("cover.blinds", "toggle")]
    [InlineData("cover.blinds", "open_cover")]
    [InlineData("cover.blinds", "close_cover")]
    [InlineData("cover.blinds", "stop_cover")]
    public void AcceptsSupportedEntityActions(string entityId, string operation)
    {
        var action = QuickActionPolicy.Create(entityId, " Action ", "", operation);
        Assert.Equal("Action", action.Label);
        Assert.StartsWith("mdi:", action.Icon);
    }

    [Theory]
    [InlineData("sensor.temperature", "toggle")]
    [InlineData("light.desk", "run")]
    [InlineData("light.desk", "volume")]
    [InlineData("media_player.speakers", "brightness")]
    [InlineData("button.restart", "toggle")]
    [InlineData("cover.blinds", "turn_on")]
    [InlineData("light.desk/../../evil", "toggle")]
    public void RejectsUnsupportedOrMalformedActions(string entityId, string operation) =>
        Assert.Throws<ArgumentException>(() => QuickActionPolicy.Create(entityId, "Action", "mdi:star", operation));

    [Fact]
    public void CoverActionsRespectReportedCapabilities()
    {
        Assert.Equal([QuickActionPolicy.Toggle, QuickActionPolicy.OpenCover, QuickActionPolicy.CloseCover],
            QuickActionPolicy.AllowedOperations("cover.blinds", 3));
        Assert.Equal([QuickActionPolicy.Toggle, QuickActionPolicy.OpenCover, QuickActionPolicy.CloseCover,
            QuickActionPolicy.StopCover], QuickActionPolicy.AllowedOperations("cover.blinds", 11));
        Assert.Equal([QuickActionPolicy.OpenCover], QuickActionPolicy.AllowedOperations("cover.blinds", 1));
        Assert.Equal([QuickActionPolicy.CloseCover], QuickActionPolicy.AllowedOperations("cover.blinds", 2));
        Assert.Equal([QuickActionPolicy.StopCover], QuickActionPolicy.AllowedOperations("cover.blinds", 8));
        Assert.Empty(QuickActionPolicy.AllowedOperations("cover.blinds", 0));
        Assert.Empty(QuickActionPolicy.AllowedOperations("cover.blinds", -1));
    }

    [Fact]
    public void RejectsTooManyAndDuplicateActions()
    {
        var action = QuickActionPolicy.Create("light.desk", "Desk", "", QuickActionPolicy.Toggle);
        Assert.Throws<InvalidDataException>(() => QuickActionPolicy.ValidateCollection([action, action]));
        Assert.Throws<InvalidDataException>(() => QuickActionPolicy.ValidateCollection(
            Enumerable.Range(0, 9).Select(i => action with { EntityId = $"light.room_{i}" })));
        Assert.Equal(2, QuickActionPolicy.ValidateCollection(
            [action, action with { Operation = QuickActionPolicy.TurnOff }]).Count);
    }

    [Fact]
    public async Task IgnoresMalformedStatesAndCoversWithoutUsableActions()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("""
            [
              null,
              "not an entity",
              {"entity_id":"cover.unsupported","state":"closed","attributes":{"supported_features":0}},
              {"entity_id":"cover.unknown","state":"closed","attributes":{}},
              {"entity_id":"cover.invalid_features","state":"closed","attributes":{"supported_features":"3"}},
              {"entity_id":"cover.negative_features","state":"closed","attributes":{"supported_features":-1}},
              {"entity_id":"cover.stop_only","state":"open","attributes":{"supported_features":8}},
              {"entity_id":"light.valid","state":"off","attributes":{"friendly_name":"A very long friendly name that exceeds forty characters"}}
            ]
            """)));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);

        var entities = await client.GetQuickActionEntitiesAsync(CancellationToken.None);

        Assert.Equal(2, entities.Count);
        Assert.Equal("light.valid", entities[0].EntityId);
        Assert.True(entities[0].Name.Length <= 40);
        Assert.Equal(new QuickActionEntity("cover.stop_only", "cover.stop_only", "mdi:blinds", "open", 8), entities[1]);
    }

    [Fact]
    public async Task RejectsMalformedEntityList()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("{}")));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetQuickActionEntitiesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetQuickActionStatesAsync(
            ["light.desk"], CancellationToken.None));
    }

    [Fact]
    public async Task ReadsOnlyRequestedQuickActionStates()
    {
        var requests = 0;
        using var handler = new Handler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/states", request.RequestUri?.AbsolutePath);
            return Task.FromResult(Response("""
                [
                  {"entity_id":"sensor.temperature","state":"19"},
                  {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk"}},
                  null,
                  {"entity_id":"switch.missing","attributes":{}},
                  {"entity_id":"switch.missing","state":"off"},
                  {"entity_id":"cover.gate","state":"closed"},
                  {"entity_id":"light.other","state":"off"}
                ]
                """));
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);

        var states = await client.GetQuickActionStatesAsync(
            ["light.desk", "cover.gate", "switch.missing"], CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(3, states.Count);
        Assert.Equal("on", states["light.desk"].State);
        Assert.Equal("closed", states["cover.gate"].State);
        Assert.Equal("off", states["switch.missing"].State);
    }

    [Fact]
    public async Task ReadsLevelsAndExposesSlidersOnlyForCapableEntities()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("""
            [
              {"entity_id":"light.dimmer","state":"on","attributes":{"supported_color_modes":["rgb"],"brightness":179}},
              {"entity_id":"light.off","state":"off","attributes":{"supported_color_modes":["brightness"]}},
              {"entity_id":"light.basic","state":"off","attributes":{"supported_color_modes":["onoff"]}},
              {"entity_id":"light.away","state":"unavailable","attributes":{"supported_color_modes":["brightness"],"brightness":90}},
              {"entity_id":"media_player.speakers","state":"playing","attributes":{"supported_features":132,"volume_level":0.35}},
              {"entity_id":"media_player.fixed","state":"playing","attributes":{"supported_features":128,"volume_level":0.7}},
              {"entity_id":"media_player.invalid","state":"playing","attributes":{"supported_features":4,"volume_level":1.5}}
            ]
            """)));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);

        var entities = (await client.GetQuickActionEntitiesAsync(CancellationToken.None))
            .ToDictionary(entity => entity.EntityId);
        Assert.Contains(QuickActionPolicy.Brightness, entities["light.dimmer"].Operations);
        Assert.Contains(QuickActionPolicy.Brightness, entities["light.off"].Operations);
        Assert.DoesNotContain(QuickActionPolicy.Brightness, entities["light.basic"].Operations);
        Assert.Equal([QuickActionPolicy.Volume], entities["media_player.speakers"].Operations);
        Assert.DoesNotContain("media_player.fixed", entities.Keys);

        var states = await client.GetQuickActionStatesAsync(
            [.. entities.Keys, "media_player.fixed"], CancellationToken.None);
        Assert.Equal(70, states["light.dimmer"].Brightness);
        Assert.Equal(0, states["light.off"].Brightness);
        Assert.Null(states["light.basic"].Brightness);
        Assert.Null(states["light.away"].Brightness);
        Assert.Equal(35, states["media_player.speakers"].Volume);
        Assert.Null(states["media_player.fixed"].Volume);
        Assert.Null(states["media_player.invalid"].Volume);
    }

    [Fact]
    public async Task EmptyQuickActionStateRequestDoesNotContactServer()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);

        Assert.Empty(await client.GetQuickActionStatesAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetQuickActionStatesAsync(
            Enumerable.Range(0, 9).Select(i => $"light.room_{i}").ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task ListsOnlySupportedEntitiesAndUsesFriendlyNameAndIcon()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/states", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(Response("""
                [
                  {"entity_id":"sensor.temperature","state":"19","attributes":{"friendly_name":"Temperature"}},
                  {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk light","icon":"mdi:desk-lamp"}},
                  {"entity_id":"button.restart","state":"unknown","attributes":{"friendly_name":"Restart","icon":"invalid"}},
                  {"entity_id":"cover.blinds","state":"closed","attributes":{"friendly_name":"Office blinds","supported_features":11}},
                  {"entity_id":"light.\\bad","state":"off","attributes":{}}
                ]
                """));
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        var entities = await client.GetQuickActionEntitiesAsync(CancellationToken.None);
        Assert.Equal(3, entities.Count);
        Assert.Equal(new QuickActionEntity("light.desk", "Desk light", "mdi:desk-lamp", "on"), entities[0]);
        Assert.Equal(new QuickActionEntity("cover.blinds", "Office blinds", "mdi:blinds", "closed", 11), entities[1]);
        Assert.Equal(new QuickActionEntity("button.restart", "Restart", "mdi:gesture-tap-button", "unknown"), entities[2]);
    }

    [Fact]
    public async Task UsesCoverDeviceClassForAutomaticIconWithoutReplacingExplicitIcon()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("""
            [
              {"entity_id":"cover.gate","state":"closed","attributes":{"device_class":"gate","supported_features":3}},
              {"entity_id":"cover.garage","state":"closed","attributes":{"device_class":"garage","supported_features":3}},
              {"entity_id":"cover.door","state":"closed","attributes":{"device_class":"door","supported_features":3}},
              {"entity_id":"cover.custom","state":"closed","attributes":{"device_class":"gate","icon":"mdi:star","supported_features":3}},
              {"entity_id":"cover.invalid","state":"closed","attributes":{"device_class":"gate","icon":"invalid","supported_features":3}}
            ]
            """)));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);

        var entities = await client.GetQuickActionEntitiesAsync(CancellationToken.None);

        Assert.Equal("mdi:gate", entities.Single(entity => entity.EntityId == "cover.gate").Icon);
        Assert.Equal("mdi:garage", entities.Single(entity => entity.EntityId == "cover.garage").Icon);
        Assert.Equal("mdi:door", entities.Single(entity => entity.EntityId == "cover.door").Icon);
        Assert.Equal("mdi:star", entities.Single(entity => entity.EntityId == "cover.custom").Icon);
        Assert.Equal("mdi:gate", entities.Single(entity => entity.EntityId == "cover.invalid").Icon);
    }

    [Theory]
    [InlineData("light.desk", "toggle", "/api/services/light/toggle")]
    [InlineData("switch.socket", "turn_off", "/api/services/switch/turn_off")]
    [InlineData("script.good_night", "run", "/api/services/script/turn_on")]
    [InlineData("scene.evening", "run", "/api/services/scene/turn_on")]
    [InlineData("button.restart", "run", "/api/services/button/press")]
    [InlineData("cover.blinds", "toggle", "/api/services/cover/toggle")]
    [InlineData("cover.blinds", "open_cover", "/api/services/cover/open_cover")]
    [InlineData("cover.blinds", "close_cover", "/api/services/cover/close_cover")]
    [InlineData("cover.blinds", "stop_cover", "/api/services/cover/stop_cover")]
    public async Task InvokesOnlyTheSelectedEntity(string entityId, string operation, string path)
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(path, request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(entityId, body.RootElement.GetProperty("entity_id").GetString());
            Assert.Single(body.RootElement.EnumerateObject());
            return Response("[]");
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await client.InvokeQuickActionAsync(QuickActionPolicy.Create(entityId, "Action", "", operation), CancellationToken.None);
    }

    [Fact]
    public async Task InvalidActionNeverReachesTheServer()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeQuickActionAsync(
            new("sensor.temperature", "Temperature", "mdi:thermometer", "toggle"), CancellationToken.None));
    }

    [Theory]
    [InlineData("light.desk", "brightness", 70, "light/turn_on", "brightness_pct", 70)]
    [InlineData("light.desk", "brightness", 0, "light/turn_off", null, 0)]
    [InlineData("media_player.speakers", "volume", 35, "media_player/volume_set", "volume_level", 0.35)]
    [InlineData("media_player.speakers", "volume", 0, "media_player/volume_set", "volume_level", 0)]
    public async Task SendsOnlyTheSelectedEntityAndLevel(string entityId, string operation, int percent,
        string service, string? property, double value)
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/services/" + service, request.RequestUri?.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(entityId, body.RootElement.GetProperty("entity_id").GetString());
            if (property is null) Assert.Single(body.RootElement.EnumerateObject());
            else
            {
                Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                Assert.Equal(value, body.RootElement.GetProperty(property).GetDouble());
            }
            return Response("[]");
        });
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        await client.InvokeQuickActionAsync(QuickActionPolicy.Create(entityId, "Level", "", operation),
            CancellationToken.None, percent);
    }

    [Theory]
    [InlineData("brightness", null)]
    [InlineData("brightness", 101)]
    [InlineData("brightness", -1)]
    [InlineData("toggle", 50)]
    public async Task RejectsMissingOutOfRangeOrUnexpectedLevelBeforeMakingRequest(string operation, int? percentage)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var client = new HaClient(new Uri("https://ha.example/"), "test-only", handler);
        var action = QuickActionPolicy.Create("light.desk", "Level", "", operation);
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.InvokeQuickActionAsync(action, CancellationToken.None, percentage));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
