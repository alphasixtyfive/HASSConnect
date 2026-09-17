using System.Text.Json;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class DashboardCatalogTests
{
    [Fact]
    public void MergesVisibleAndHiddenDashboardsByPath()
    {
        using var panels = JsonDocument.Parse("""
            {
              "lovelace": { "component_name": "lovelace", "title": "Overview", "url_path": "lovelace" },
              "energy": { "component_name": "energy", "title": "Energy", "url_path": "energy" },
              "dashboard-cameras": { "component_name": "lovelace", "title": "Old title", "url_path": "dashboard-cameras" }
            }
            """);
        using var metadata = JsonDocument.Parse("""
            [
              { "title": "Cameras", "url_path": "dashboard-cameras" },
              { "title": "Hidden", "url_path": "dashboard-hidden", "show_in_sidebar": false }
            ]
            """);

        var dashboards = DashboardCatalog.Parse(panels.RootElement, metadata.RootElement);

        Assert.Equal(3, dashboards.Count);
        Assert.Contains(new HomeAssistantDashboard("Overview", "/lovelace"), dashboards);
        Assert.Contains(new HomeAssistantDashboard("Cameras", "/dashboard-cameras"), dashboards);
        Assert.Contains(new HomeAssistantDashboard("Hidden", "/dashboard-hidden"), dashboards);
        Assert.DoesNotContain(dashboards, dashboard => dashboard.Path == "/energy");
    }

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    public void RejectsUnexpectedApiShapes(string panelsJson, string metadataJson)
    {
        using var panels = JsonDocument.Parse(panelsJson);
        using var metadata = JsonDocument.Parse(metadataJson);
        Assert.Throws<InvalidDataException>(() => DashboardCatalog.Parse(panels.RootElement, metadata.RootElement));
    }
}
