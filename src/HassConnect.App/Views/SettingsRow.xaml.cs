using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class SettingsRow : UserControl
{
    private bool _updating = true;
    private UIElement? _trailingContent;

    public SettingsRow()
    {
        InitializeComponent();
        _updating = false;
    }

    public string Title
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            AutomationProperties.SetName(Switch, value);
        }
    }

    public string Glyph
    {
        get => RowIcon.Glyph;
        set
        {
            RowIcon.Glyph = value;
            RowIcon.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string ValueText
    {
        get => ValueLabel.Text;
        set
        {
            ValueLabel.Text = value;
            ValueLabel.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetItemStatus(Switch, value);
            ToolTipService.SetToolTip(ValueLabel, string.IsNullOrEmpty(value) ? null : value);
            UpdateValueLayout();
        }
    }

    public bool IsOn
    {
        get => Switch.IsOn;
        set
        {
            _updating = true;
            try { Switch.IsOn = value; }
            finally { _updating = false; }
        }
    }

    public bool IsToggleEnabled
    {
        get => Switch.IsEnabled;
        set => Switch.IsEnabled = value;
    }

    public bool ShowDivider
    {
        get => Row.BorderThickness.Bottom > 0;
        set => Row.BorderThickness = new Thickness(0, 0, 0, value ? 1 : 0);
    }

    public UIElement? TrailingContent
    {
        get => _trailingContent;
        set
        {
            _trailingContent = value;
            TrailingPresenter.Content = value ?? Switch;
        }
    }

    public event EventHandler? ToggleChanged;

    private void Row_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateValueLayout();

    private void UpdateValueLayout()
    {
        if (TitleText is null || ValueLabel is null) return;

        var stacked = Row.ActualWidth < 440 && ValueLabel.Visibility == Visibility.Visible;
        Grid.SetColumn(ValueLabel, stacked ? 0 : 1);
        Grid.SetRow(ValueLabel, stacked ? 1 : 0);
        Grid.SetColumnSpan(TitleText, stacked ? 2 : 1);
        Grid.SetColumnSpan(ValueLabel, stacked ? 2 : 1);
        TitleText.Margin = stacked ? new Thickness(0, 8, 0, 2) : new Thickness(0, 8, 16, 8);
        ValueLabel.Margin = stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0);
        ValueLabel.MaxWidth = stacked ? double.PositiveInfinity : 180;
        ValueLabel.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    private void Switch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updating)
            ToggleChanged?.Invoke(this, EventArgs.Empty);
    }
}
