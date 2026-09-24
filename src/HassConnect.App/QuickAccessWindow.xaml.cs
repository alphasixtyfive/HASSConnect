using System.Runtime.InteropServices;
using HassConnect.Core;
using HassConnect.App.Views;
using HassConnect.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace HassConnect.App;

internal sealed partial class QuickAccessWindow : Window
{
    private const int WindowWidth = 360;
    private const int EdgeGap = 8;
    private const uint DwmCornerPreference = 33;
    private const uint DwmBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint FrameChangedWithoutMoving = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;
    private readonly nint _windowHandle;
    private readonly SubclassProc _frameCallback;
    private readonly List<ActionTile> _tiles = [];
    private bool _actionPending;
    private bool _isConnected;
    private int _showVersion;

    internal event Action<QuickActionDefinition>? ActionRequested;
    internal event Action? EditRequested;
    internal event Action? OpenHomeAssistantRequested;

    internal bool IsVisible { get; private set; }

    internal QuickAccessWindow()
    {
        InitializeComponent();
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        _frameCallback = NativeFrameProc;
        if (!SetWindowSubclass(_windowHandle, _frameCallback, 2, 0))
            throw new InvalidOperationException("Unable to configure the quick access window frame.");
        _ = SetWindowPos(_windowHandle, 0, 0, 0, 0, 0, FrameChangedWithoutMoving);
        AppWindow.IsShownInSwitchers = false;
        ConfigureDwmFrame();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                Hide();
            else
                ConfigureDwmFrame();
        };
    }

    internal void SetTheme(ElementTheme theme) => Surface.RequestedTheme = theme;

    internal void ShowNearTray(IReadOnlyList<QuickActionDefinition> actions, bool isConnected) =>
        Show(actions, isConnected, fromShortcut: false);

    internal void ShowFromShortcut(IReadOnlyList<QuickActionDefinition> actions, bool isConnected) =>
        Show(actions, isConnected, fromShortcut: true);

    private void Show(IReadOnlyList<QuickActionDefinition> actions, bool isConnected, bool fromShortcut)
    {
        var showVersion = ++_showVersion;
        PopulateActions(actions, isConnected);
        if (fromShortcut) PositionForShortcut(actions.Count, !isConnected);
        else PositionNearCursor(actions.Count, !isConnected);
        IsVisible = true;
        AppWindow.Show();
        WindowActivation.RestoreAndActivate(_windowHandle);
        Activate();
        ConfigureDwmFrame();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsVisible || showVersion != _showVersion) return;
            Control focus = _tiles.FirstOrDefault(tile => tile.Button.IsEnabled)?.Button
                ?? (EmptyState.Visibility == Visibility.Visible ? (Control)EmptyActionButton : SettingsButton);
            // Pointer focus keeps the tray popup ready without a keyboard focus ring.
            focus.Focus(fromShortcut ? FocusState.Keyboard : FocusState.Pointer);
        });
    }

    internal void Hide()
    {
        if (!IsVisible) return;
        _showVersion++;
        _actionPending = false;
        IsVisible = false;
        AppWindow.Hide();
    }

    private void ConfigureDwmFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var rounded = 2u;
        _ = DwmSetWindowAttribute(_windowHandle, DwmCornerPreference, ref rounded, sizeof(uint));
        var color = DwmColorNone;
        _ = DwmSetWindowAttribute(_windowHandle, DwmBorderColor, ref color, sizeof(uint));
    }

    private nint NativeFrameProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == 0x0083) return 0; // WM_NCCALCSIZE: use the full window as client area.
        if (message == 0x0082) RemoveWindowSubclass(window, _frameCallback, id); // WM_NCDESTROY.
        return DefSubclassProc(window, message, wParam, lParam);
    }

    internal void ShowError(string message)
    {
        _actionPending = false;
        foreach (var tile in _tiles.Where(tile => tile.IsOptimistic))
        {
            UpdateTile(tile, tile.PreviousState);
            tile.IsOptimistic = false;
        }
        SetActionsEnabled(_isConnected);
        ConnectionMessage.Text = message;
        ConnectionMessage.Visibility = Visibility.Visible;
    }

    private void PopulateActions(IReadOnlyList<QuickActionDefinition> actions, bool isConnected)
    {
        _isConnected = isConnected;
        _actionPending = false;
        ActionsGrid.Children.Clear();
        ActionsGrid.RowDefinitions.Clear();
        _tiles.Clear();
        var count = Math.Min(actions.Count, 8);
        ConnectionMessage.Text = "Home Assistant is disconnected. Actions are unavailable.";
        ConnectionMessage.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionsScroll.VerticalScrollBarVisibility = count == 0
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        EmptyMessage.Text = isConnected
            ? "No quick actions yet."
            : "Connect to Home Assistant to add actions.";
        EmptyActionButton.Content = isConnected ? "Add an action" : "Open settings";

        for (var index = 0; index < count; index++)
        {
            if (index % 2 == 0)
                ActionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var action = actions[index];
            var button = CreateActionButton(action, isConnected);
            Grid.SetRow(button, index / 2);
            Grid.SetColumn(button, index % 2);
            ActionsGrid.Children.Add(button);
        }
    }

    private Button CreateActionButton(QuickActionDefinition action, bool isConnected)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = QuickActionIcons.GlyphFor(action.Icon, action.EntityId), FontSize = 20 };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var details = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = action.Label,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        details.Children.Add(label);
        var status = new TextBlock
        {
            FontSize = 12,
            Foreground = SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        details.Children.Add(status);
        Grid.SetColumn(details, 1);
        content.Children.Add(details);

        var button = new Button
        {
            Content = content,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
            IsEnabled = isConnected
        };
        var tile = new ActionTile(action, button, icon, status);
        _tiles.Add(tile);
        UpdateTile(tile, null);
        if (isConnected && action.EntityId.Split('.')[0] is not ("script" or "scene" or "button"))
            status.Text = "Loading…";
        button.Click += (_, _) =>
        {
            if (_actionPending) return;
            _actionPending = true;
            tile.PreviousState = tile.State;
            var predicted = PredictState(action.Operation, action.EntityId, tile.State);
            if (predicted is not null)
            {
                UpdateTile(tile, predicted);
                tile.IsOptimistic = true;
            }
            ActionRequested?.Invoke(action);
        };
        button.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) => MoveTileFocus(tile, args)), handledEventsToo: true);
        return button;
    }

    private void MoveTileFocus(ActionTile tile, KeyRoutedEventArgs args)
    {
        var index = _tiles.IndexOf(tile);
        if (index < 0) return;

        var next = args.Key switch
        {
            Windows.System.VirtualKey.Left when index % 2 == 1 => index - 1,
            Windows.System.VirtualKey.Right when index % 2 == 0 => index + 1,
            Windows.System.VirtualKey.Up => index - 2,
            Windows.System.VirtualKey.Down => index + 2,
            _ => -1
        };
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or
            Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        args.Handled = true;

        if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            var step = args.Key == Windows.System.VirtualKey.Up ? -2 : 2;
            while (next >= 0 && next < _tiles.Count && !_tiles[next].Button.IsEnabled)
                next += step;
        }
        if (next >= _tiles.Count && args.Key == Windows.System.VirtualKey.Down && index % 2 == 1)
            next = _tiles.Count - 1; // The final row may have only a left tile.
        if (next >= 0 && next < _tiles.Count && _tiles[next].Button.IsEnabled)
            _tiles[next].Button.Focus(FocusState.Keyboard);
    }

    internal void CompleteAction()
    {
        _actionPending = false;
        SetActionsEnabled(_isConnected);
        ConnectionMessage.Visibility = _isConnected ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetEntityStates(IReadOnlyDictionary<string, string>? states, bool preserveOptimistic = false)
    {
        if (!IsVisible || _actionPending) return;
        if (states is null)
        {
            foreach (var tile in _tiles)
            {
                if (tile.IsOptimistic && !preserveOptimistic)
                {
                    tile.IsOptimistic = false;
                    UpdateTile(tile, tile.PreviousState);
                }
                else if (tile.State is null)
                {
                    UpdateTile(tile, null);
                }
            }
            SetActionsEnabled(_isConnected);
            return;
        }

        foreach (var tile in _tiles)
        {
            var state = states.TryGetValue(tile.Action.EntityId, out var found)
                ? found : null;
            if (preserveOptimistic && tile.IsOptimistic && state == tile.PreviousState)
                continue;
            tile.IsOptimistic = false;
            UpdateTile(tile, state);
        }
        if (!_actionPending) SetActionsEnabled(_isConnected);
    }

    private void UpdateTile(ActionTile tile, string? state)
    {
        tile.State = state;
        var domain = tile.Action.EntityId.Split('.')[0];
        var status = domain switch
        {
            "script" or "scene" or "button" => "Run",
            _ when state is null => "Status unavailable",
            "cover" => state switch
            {
                "open" => "Open", "closed" => "Closed", "opening" => "Opening", "closing" => "Closing",
                "unavailable" => "Unavailable", _ => "Unknown"
            },
            _ => state switch
            {
                "on" => "On", "off" => "Off", "unavailable" => "Unavailable", _ => "Unknown"
            }
        };
        tile.Status.Text = status;
        tile.Icon.Foreground = state is "on" or "open" or "opening" ? AccentBrush : PrimaryBrush;
        var description = $"{QuickActionLabels.Operation(tile.Action.Operation)} {tile.Action.Label}. {status}.";
        AutomationProperties.SetName(tile.Button, description);
        ToolTipService.SetToolTip(tile.Button, $"{description} {tile.Action.EntityId}");
    }

    private static Brush? AccentBrush => Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var value)
        ? value as Brush : null;

    private static Brush? PrimaryBrush => Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var value)
        ? value as Brush : null;

    private static Brush? SecondaryBrush => Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var value)
        ? value as Brush : null;

    private sealed record ActionTile(QuickActionDefinition Action, Button Button, FontIcon Icon, TextBlock Status)
    {
        public string? State { get; set; }
        public string? PreviousState { get; set; }
        public bool IsOptimistic { get; set; }
    }

    private static string? PredictState(string operation, string entityId, string? current) => operation switch
    {
        QuickActionPolicy.Toggle when entityId.StartsWith("cover.", StringComparison.Ordinal) =>
            current switch { "closed" => "opening", "open" => "closing", _ => null },
        QuickActionPolicy.Toggle => current switch { "on" => "off", "off" => "on", _ => null },
        QuickActionPolicy.TurnOn => "on",
        QuickActionPolicy.TurnOff => "off",
        QuickActionPolicy.OpenCover => "opening",
        QuickActionPolicy.CloseCover => "closing",
        _ => null
    };

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var tile in _tiles)
            tile.Button.IsEnabled = enabled && tile.State != "unavailable";
    }

    private void PositionNearCursor(int actionCount, bool hasMessage)
    {
        if (!GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, 2); // Nearest monitor.
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.WorkArea;
        var (width, height) = ResizeForMonitor(monitor, work, actionCount, hasMessage);

        var x = Math.Clamp(cursor.X - width + 24, work.Left + EdgeGap, work.Right - width - EdgeGap);
        var above = cursor.Y - work.Top;
        var below = work.Bottom - cursor.Y;
        var y = below >= height + 12 && (below >= above || above < height + 12)
            ? cursor.Y + 12
            : cursor.Y - height - 12;
        y = Math.Clamp(y, work.Top + EdgeGap, work.Bottom - height - EdgeGap);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void PositionForShortcut(int actionCount, bool hasMessage)
    {
        var foreground = GetForegroundWindow();
        var monitor = foreground != 0 ? MonitorFromWindow(foreground, 2) : 0;
        if (monitor == 0 && GetCursorPos(out var cursor)) monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.WorkArea;
        var (width, height) = ResizeForMonitor(monitor, work, actionCount, hasMessage);
        var x = work.Left + (work.Right - work.Left - width) / 2;
        var y = work.Top + (work.Bottom - work.Top - height) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private (int Width, int Height) ResizeForMonitor(nint monitor, NativeRect work,
        int actionCount, bool hasMessage)
    {
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0
            ? dpi / 96d
            : GetDpiForWindow(_windowHandle) / 96d;
        var rows = (Math.Min(actionCount, 8) + 1) / 2;
        var desiredHeight = (rows == 0 ? 210 : 112 + rows * 72) + (hasMessage ? 26 : 0);
        var width = Math.Min((int)Math.Ceiling(WindowWidth * scale), work.Right - work.Left - EdgeGap * 2);
        var height = Math.Min((int)Math.Ceiling(desiredHeight * scale), work.Bottom - work.Top - EdgeGap * 2);
        AppWindow.Resize(new SizeInt32(width, height));
        return (width, height);
    }

    private void Surface_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        Hide();
        args.Handled = true;
    }

    private void Edit_Click(object sender, RoutedEventArgs args)
    {
        Hide();
        EditRequested?.Invoke();
    }

    private void OpenHomeAssistant_Click(object sender, RoutedEventArgs args)
    {
        Hide();
        OpenHomeAssistantRequested?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint reference);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

}
