using System.Runtime.InteropServices;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HassConnect.App.Views;

public sealed partial class QuickAccessSettingsView : UserControl
{
    private QuickActionDefinition[] _displayedActions = [];
    private bool _registered;
    private bool _recordingShortcut;

    public event Action? AddRequested;
    public event Action<int>? EditRequested;
    public event Action<int>? RemoveRequested;
    public event Action<int, int>? MoveRequested;
    public event Action<QuickAccessShortcut?>? ShortcutChanged;

    public QuickAccessSettingsView() => InitializeComponent();

    public void Update(Settings settings, bool registered)
    {
        var actions = settings.QuickActions;
        if (!_displayedActions.SequenceEqual(actions) || _registered != registered)
        {
            _displayedActions = [.. actions];
            _registered = registered;
            RebuildRows();
        }

        CountText.Text = $"{actions.Count} of {QuickActionPolicy.MaximumActions} actions";
        AddButton.IsEnabled = registered && actions.Count < QuickActionPolicy.MaximumActions;
        StatusText.Visibility = registered ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutValue.Text = QuickAccessShortcutPolicy.Display(settings.QuickAccessShortcut);
        ClearShortcutButton.IsEnabled = settings.QuickAccessShortcut is not null;
    }

    public void SetShortcutStatus(string? message)
    {
        ShortcutStatusText.Text = message ?? "";
        ShortcutStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RecordShortcut_Click(object sender, RoutedEventArgs args)
    {
        _recordingShortcut = true;
        RecordShortcutButton.Content = "Press shortcut…";
        SetShortcutStatus("Press Ctrl + Shift or Alt + Shift with a letter or number. Esc cancels.");
        RecordShortcutButton.Focus(FocusState.Programmatic);
    }

    private void RecordShortcut_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_recordingShortcut) return;
        if (args.Key == Windows.System.VirtualKey.Tab)
        {
            StopRecording();
            SetShortcutStatus(null);
            return;
        }

        args.Handled = true;
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            StopRecording();
            SetShortcutStatus(null);
            return;
        }

        var key = (int)args.Key;
        if (key is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')) return;
        var shortcut = new QuickAccessShortcut(key,
            GetKeyState(0x11) < 0, GetKeyState(0x12) < 0, GetKeyState(0x10) < 0);
        try { QuickAccessShortcutPolicy.Validate(shortcut); }
        catch (InvalidDataException ex)
        {
            SetShortcutStatus(ex.Message);
            return;
        }

        StopRecording();
        SetShortcutStatus(null);
        ShortcutChanged?.Invoke(shortcut);
    }

    private void RecordShortcut_LostFocus(object sender, RoutedEventArgs args)
    {
        if (!_recordingShortcut) return;
        StopRecording();
        SetShortcutStatus(null);
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs args)
    {
        StopRecording();
        SetShortcutStatus(null);
        ShortcutChanged?.Invoke(null);
    }

    private void StopRecording()
    {
        _recordingShortcut = false;
        RecordShortcutButton.Content = "Set shortcut";
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetKeyState(int virtualKey);

    private void RebuildRows()
    {
        ActionRows.Children.Clear();
        for (var index = 0; index < _displayedActions.Length; index++)
        {
            var action = _displayedActions[index];
            var rowIndex = index;
            var rowActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowActions.Children.Add(CreateIconButton("\uE74A", $"Move {action.Label} up", index > 0,
                () => MoveRequested?.Invoke(rowIndex, -1)));
            rowActions.Children.Add(CreateIconButton("\uE74B", $"Move {action.Label} down", index < _displayedActions.Length - 1,
                () => MoveRequested?.Invoke(rowIndex, 1)));
            rowActions.Children.Add(CreateIconButton("\uE713", $"Edit {action.Label}", _registered,
                () => EditRequested?.Invoke(rowIndex)));
            rowActions.Children.Add(CreateIconButton("\uE74D", $"Remove {action.Label}", true,
                () => RemoveRequested?.Invoke(rowIndex)));

            var row = new SettingsRow
            {
                Title = action.Label,
                ValueText = $"{QuickActionLabels.Operation(action.Operation)} · {action.EntityId}",
                Glyph = QuickActionIcons.GlyphFor(action.Icon, action.EntityId),
                AlwaysStackValue = true,
                TrailingContent = rowActions,
                ShowDivider = index < _displayedActions.Length - 1
            };
            ToolTipService.SetToolTip(row, action.EntityId);
            AutomationProperties.SetHelpText(row, $"{index + 1} of {_displayedActions.Length} in the tray popup.");
            ActionRows.Children.Add(row);
        }
    }

    private static Button CreateIconButton(string glyph, string name, bool enabled, Action clicked)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            Padding = new Thickness(0),
            IsEnabled = enabled,
            UseSystemFocusVisuals = true,
            FocusVisualMargin = new Thickness(-2),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => clicked();
        return button;
    }

    private void Add_Click(object sender, RoutedEventArgs args) => AddRequested?.Invoke();
}
