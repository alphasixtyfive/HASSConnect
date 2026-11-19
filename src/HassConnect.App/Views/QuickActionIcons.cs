using HassConnect.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace HassConnect.App.Views;

public sealed record QuickIconChoice(string Id, string Name, string? Glyph, string Category, string? Path = null)
{
    public IconElement Icon => new IconSourceElement { IconSource = CreateSource(20), Width = 20, Height = 20 };
    public override string ToString() => Name;

    public IconSource CreateSource(double size)
    {
        if (Path is null) return QuickActionIcons.FontSource(Glyph!, size);
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), $"F1 {Path}");
        var scale = size / 24;
        geometry.Transform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        return new PathIconSource { Data = geometry };
    }
}

internal static partial class QuickActionIcons
{
    // Names and glyphs are from Microsoft's Segoe Fluent Icons catalog. The mdi: IDs
    // keep existing quick actions compatible with Home Assistant icon settings.
    public static IReadOnlyList<QuickIconChoice> Choices { get; } = Array.AsReadOnly<QuickIconChoice>([
        new("mdi:home", "Home", "\uE80F", "Home"),
        new("mdi:map-marker", "Location", "\uE707", "Home"),
        new("mdi:car", "Car", "\uE804", "Home"),
        new("mdi:lock", "Lock", "\uE72E", "Home"),
        new("mdi:lock-open", "Unlock", "\uE785", "Home"),
        new("mdi:window-open", "Panel", "\uE8A0", "Home"),
        new("mdi:earth", "World", "\uE909", "Home"),
        new("mdi:calendar", "Calendar", "\uE787", "Home"),
        new("mdi:cloud", "Cloud", "\uE753", "Home"),
        new("mdi:brightness-6", "Brightness", "\uE706", "Home"),
        new("mdi:account-group", "People", "\uE716", "Home"),
        new("mdi:cart", "Shopping", "\uE7BF", "Home"),

        new("mdi:lightbulb", "Light", "\uEA80", "Devices"),
        new("mdi:power", "Power", "\uE7E8", "Devices"),
        new("mdi:gesture-tap-button", "Tap", "\uE8B0", "Devices"),
        new("mdi:wifi", "Wi-Fi", "\uE701", "Devices"),
        new("mdi:bluetooth", "Bluetooth", "\uE702", "Devices"),
        new("mdi:phone", "Phone", "\uE717", "Devices"),
        new("mdi:camera", "Camera", "\uE722", "Devices"),
        new("mdi:television", "TV", "\uE7F4", "Devices"),
        new("mdi:speaker", "Speakers", "\uE7F5", "Devices"),
        new("mdi:headphones", "Headphones", "\uE7F6", "Devices"),
        new("mdi:gamepad", "Game", "\uE7FC", "Devices"),
        new("mdi:printer", "Printer", "\uE749", "Devices"),
        new("mdi:tablet", "Tablet", "\uE70A", "Devices"),
        new("mdi:flashlight", "Flashlight", "\uE754", "Devices"),
        new("mdi:laptop", "Laptop", "\uE7F8", "Devices"),
        new("mdi:microphone", "Microphone", "\uE720", "Devices"),

        new("mdi:play", "Play", "\uE768", "Media"),
        new("mdi:pause", "Pause", "\uE769", "Media"),
        new("mdi:stop", "Stop", "\uE71A", "Media"),
        new("mdi:volume-high", "Volume", "\uE767", "Media"),
        new("mdi:volume-off", "Mute", "\uE74F", "Media"),
        new("mdi:video", "Video", "\uE714", "Media"),
        new("mdi:music", "Audio", "\uE8D6", "Media"),
        new("mdi:movie-open", "Movies", "\uE8B2", "Media"),

        new("mdi:star", "Star", "\uE734", "Symbols"),
        new("mdi:flag", "Flag", "\uE7C1", "Symbols"),
        new("mdi:check", "Check", "\uE73E", "Symbols"),
        new("mdi:alert", "Warning", "\uE7BA", "Symbols"),
        new("mdi:cog", "Settings", "\uE713", "Symbols"),
        new("mdi:clock-outline", "Clock", "\uE917", "Symbols"),
        new("mdi:magnify", "Search", "\uE721", "Symbols"),
        new("mdi:plus", "Add", "\uE710", "Symbols"),
        new("mdi:pencil", "Edit", "\uE70F", "Symbols"),
        new("mdi:pin", "Pin", "\uE718", "Symbols"),
        new("mdi:file-document-outline", "Document", "\uE8A5", "Symbols"),
        new("mdi:refresh", "Refresh", "\uE72C", "Symbols"),
        new("mdi:palette", "Color", "\uE790", "Symbols"),
        .. MaterialDesignChoices()
    ]);

    private static readonly IReadOnlyDictionary<string, QuickIconChoice> IconsById =
        Choices.ToDictionary(choice => choice.Id, StringComparer.Ordinal);

    public static IconSource SourceFor(string? icon, string entityId, double size)
    {
        var selected = string.IsNullOrWhiteSpace(icon) ? QuickActionPolicy.DefaultIcon(entityId) : icon;
        if (IconsById.TryGetValue(selected, out var choice)) return choice.CreateSource(size);
        return FontSource(FallbackGlyph(selected, entityId), size);
    }

    internal static string EntityGlyphFor(string? icon, string entityId)
    {
        var selected = string.IsNullOrWhiteSpace(icon) ? QuickActionPolicy.DefaultIcon(entityId) : icon;
        return IconsById.GetValueOrDefault(selected)?.Glyph ?? FallbackGlyph(selected, entityId);
    }

    private static string FallbackGlyph(string selected, string entityId) => selected switch
    {
        "mdi:button-pointer" => "\uE8B0",
        "mdi:home-assistant" => "\uE80F",
        "mdi:blinds" or "mdi:door" or "mdi:door-open" => "\uE8A0",
        "mdi:gate" or "mdi:gate-open" => "\uE90D",
        "mdi:garage" or "mdi:garage-open" => "\uE804",
        "mdi:music-note" => "\uE8D6",
        "mdi:toggle-switch" or "mdi:toggle-switch-variant" => GlyphForDomain(entityId),
        "mdi:check-circle-outline" => "\uE73A",
        "mdi:script-text" => "\uE8A5",
        _ => GlyphForDomain(entityId)
    };

    internal static FontIconSource FontSource(string glyph, double size) => new()
    {
        Glyph = glyph,
        FontSize = size,
        FontFamily = new FontFamily("Segoe Fluent Icons")
    };

    private static string GlyphForDomain(string entityId) => entityId.Split('.')[0] switch
    {
        "light" => "\uEA80",
        "switch" => "\uE7E8",
        "input_boolean" => "\uE73A",
        "fan" => "\uE72C",
        "cover" => "\uE8A0",
        "scene" => "\uE790",
        "script" => "\uE8A5",
        "button" => "\uE8B0",
        _ => "\uE7C3"
    };
}
