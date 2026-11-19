using HassConnect.Core;
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

    internal void SetAction(QuickActionDefinition action, bool compact)
    {
        LabelText.Text = action.Label;
        var iconSize = compact ? 16 : 20;
        TileIcon.Width = TileIcon.Height = iconSize;
        TileIcon.IconSource = QuickActionIcons.SourceFor(action.Icon, action.EntityId, iconSize);
        ToolTipService.SetToolTip(LabelText, action.Label);
        AutomationProperties.SetName(LevelSlider, action.Label);

        if (!compact) return;
        TileBorder.Padding = new Thickness(8, 9, 8, 7);
        TileContent.ColumnSpacing = 5;
        IconColumn.Width = new GridLength(18);
        LabelText.FontSize = 13;
        PercentageText.FontSize = 12;
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
