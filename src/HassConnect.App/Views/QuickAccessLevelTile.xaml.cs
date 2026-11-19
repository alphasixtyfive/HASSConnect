using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class QuickAccessLevelTile : UserControl
{
    private bool _updatingLevel;

    internal Slider Input => LevelSlider;
    internal event Action<int>? LevelChanged;

    public QuickAccessLevelTile()
    {
        InitializeComponent();
        LevelSlider.ValueChanged += (_, args) =>
        {
            if (_updatingLevel) return;
            var level = (int)args.NewValue;
            PercentageText.Text = $"{level}%";
            LevelChanged?.Invoke(level);
        };
    }

    internal void SetLabel(string label, string glyph)
    {
        LabelText.Text = label;
        GlyphIcon.Glyph = glyph;
        ToolTipService.SetToolTip(LabelText, label);
        AutomationProperties.SetName(LevelSlider, label);
    }

    internal void UpdateLevel(int? level, string missingText)
    {
        _updatingLevel = true;
        try
        {
            LevelSlider.Value = level ?? 0;
            PercentageText.Text = level is { } percent ? $"{percent}%" : missingText;
        }
        finally { _updatingLevel = false; }
    }
}
