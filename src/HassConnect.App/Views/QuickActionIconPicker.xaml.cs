using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class QuickActionIconPicker : UserControl
{
    private string _entityId = "";
    private string _automaticIcon = "";
    private string _selectedIcon = "";

    public string SelectedIcon => _selectedIcon;

    public QuickActionIconPicker()
    {
        InitializeComponent();
        IconCategory.SelectedIndex = 0;
        UpdatePreview();
    }

    public void SetEntity(string? entityId, string? automaticIcon)
    {
        _entityId = entityId ?? "";
        _automaticIcon = string.IsNullOrWhiteSpace(automaticIcon)
            ? QuickActionPolicy.DefaultIcon(entityId) : automaticIcon;
        UpdatePreview();
    }

    public void SelectIcon(string? icon)
    {
        _selectedIcon = icon ?? "";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var icon = string.IsNullOrEmpty(_selectedIcon) ? _automaticIcon : _selectedIcon;
        var choice = QuickActionIcons.Choices.FirstOrDefault(item => item.Id == _selectedIcon);
        var label = string.IsNullOrEmpty(_selectedIcon) ? "Automatic" : choice?.Name ?? "Current icon";
        SelectedGlyph.Glyph = QuickActionIcons.GlyphFor(icon, _entityId);
        AutomaticGlyph.Glyph = QuickActionIcons.GlyphFor(_automaticIcon, _entityId);
        AutomaticCheck.Visibility = string.IsNullOrEmpty(_selectedIcon) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(AutomaticButton, string.IsNullOrEmpty(_selectedIcon)
            ? "Automatic icon, selected" : "Automatic icon");
        SelectedLabel.Text = label;
        ToolTipService.SetToolTip(PickerButton, choice is null && _selectedIcon.Length > 0
            ? _selectedIcon : label);
        AutomationProperties.SetName(PickerButton, $"Icon: {label}. Choose icon");
    }

    private void IconFlyout_Opened(object sender, object args)
    {
        IconSearch.Text = "";
        IconCategory.SelectedIndex = 0;
        FilterIcons();
        IconGrid.SelectedItem = QuickActionIcons.Choices.FirstOrDefault(item => item.Id == _selectedIcon);
        IconSearch.Focus(FocusState.Programmatic);
    }

    private void IconFilter_Changed(object sender, TextChangedEventArgs args) => FilterIcons();

    private void IconCategory_Changed(object sender, SelectionChangedEventArgs args) => FilterIcons();

    private void FilterIcons()
    {
        if (IconSearch is null || IconCategory is null || IconGrid is null || NoIconsMessage is null) return;
        var query = IconSearch.Text.Trim();
        var category = (IconCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var choices = QuickActionIcons.Choices.Where(choice =>
            (category.Length == 0 || choice.Category == category) &&
            (query.Length == 0 || choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             choice.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        IconGrid.ItemsSource = choices;
        IconGrid.Visibility = choices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IconGrid.SelectedItem = choices.FirstOrDefault(choice => choice.Id == _selectedIcon);
        NoIconsMessage.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Automatic_Click(object sender, RoutedEventArgs args)
    {
        SelectIcon("");
        IconFlyout.Hide();
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not QuickIconChoice choice) return;
        SelectIcon(choice.Id);
        IconFlyout.Hide();
    }
}
