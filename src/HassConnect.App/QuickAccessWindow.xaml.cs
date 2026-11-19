using System.Runtime.InteropServices;
using HassConnect.Core;
using HassConnect.App.Views;
using HassConnect.App.Services;
using Microsoft.UI.Dispatching;
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
    private readonly DispatcherQueueTimer _levelCommitTimer;
    private ActionTile? _pendingAction;
    private bool _isConnected;
    private int _showVersion;

    internal event Action<QuickActionDefinition, int?>? ActionRequested;
    internal event Action? EditRequested;
    internal event Action? OpenHomeAssistantRequested;
    internal event Action? Hidden;

    internal bool IsVisible { get; private set; }

    internal QuickAccessWindow()
    {
        InitializeComponent();
        _levelCommitTimer = DispatcherQueue.CreateTimer();
        _levelCommitTimer.Interval = TimeSpan.FromMilliseconds(200);
        _levelCommitTimer.IsRepeating = false;
        _levelCommitTimer.Tick += (_, _) => CommitPendingLevel();
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
        if (fromShortcut) PositionForShortcut(!isConnected);
        else PositionNearCursor(!isConnected);
        IsVisible = true;
        AppWindow.Show();
        WindowActivation.RestoreAndActivate(_windowHandle);
        Activate();
        ConfigureDwmFrame();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsVisible || showVersion != _showVersion) return;
            Control focus = _tiles.FirstOrDefault(tile => tile.Input.IsEnabled)?.Input
                ?? (EmptyState.Visibility == Visibility.Visible ? (Control)EmptyActionButton : SettingsButton);
            // Pointer focus keeps the tray popup ready without a keyboard focus ring.
            focus.Focus(fromShortcut ? FocusState.Keyboard : FocusState.Pointer);
        });
    }

    internal void Hide(bool commitPendingLevel = true)
    {
        if (!IsVisible) return;
        _levelCommitTimer.Stop();
        if (commitPendingLevel) CommitPendingLevel();
        _showVersion++;
        IsVisible = false;
        AppWindow.Hide();
        Hidden?.Invoke();
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
        _levelCommitTimer.Stop();
        if (_pendingAction is { IsOptimistic: true } failed)
        {
            failed.IsOptimistic = false;
            UpdateTile(failed, failed.PreviousState);
        }
        _pendingAction = null;
        foreach (var tile in _tiles.Where(tile => tile.PendingLevel is not null))
        {
            tile.PendingLevel = null;
            UpdateTile(tile, tile.State);
        }
        SetActionsEnabled(_isConnected);
        ConnectionMessage.Text = message;
        ConnectionMessage.Visibility = Visibility.Visible;
    }

    private void PopulateActions(IReadOnlyList<QuickActionDefinition> actions, bool isConnected)
    {
        _isConnected = isConnected;
        _pendingAction = null;
        _levelCommitTimer.Stop();
        ActionsGrid.Children.Clear();
        ActionsGrid.RowDefinitions.Clear();
        _tiles.Clear();
        var count = Math.Min(actions.Count, QuickActionPolicy.MaximumActions);
        ConnectionMessage.Text = "Home Assistant is disconnected. Actions are unavailable.";
        ConnectionMessage.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionsScroll.VerticalScrollBarVisibility = count == 0
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        EmptyMessage.Text = isConnected
            ? "No quick actions yet."
            : "Connect to Home Assistant to add actions.";
        EmptyActionButton.Content = isConnected ? "Add an action" : "Open settings";

        var columns = ActionsGrid.ColumnDefinitions.Count;
        var column = 0;
        foreach (var action in actions.Take(count))
        {
            var tile = QuickActionPolicy.IsLevel(action.Operation)
                ? CreateLevelTile(action) : CreateActionButton(action, isConnected);
            var span = action.Size switch
            {
                QuickActionSize.Compact => 1,
                QuickActionSize.Wide => columns,
                _ => tile.LevelTile is null ? 1 : columns
            };
            if (column == 0 || column + span > columns)
            {
                ActionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                column = 0;
            }

            Grid.SetRow(tile.Element, ActionsGrid.RowDefinitions.Count - 1);
            Grid.SetColumn(tile.Element, column);
            Grid.SetColumnSpan(tile.Element, span);
            tile.LevelTile?.SetAction(action, compact: span == 1);
            ActionsGrid.Children.Add(tile.Element);
            _tiles.Add(tile);
            column = (column + span) % columns;
        }
    }

    private ActionTile CreateActionButton(QuickActionDefinition action, bool isConnected)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new IconSourceElement
        {
            IconSource = QuickActionIcons.SourceFor(action.Icon, action.EntityId, 20),
            Width = 20,
            Height = 20
        };
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
            MinHeight = 64,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8),
            IsEnabled = isConnected
        };
        var tile = new ActionTile(action, button, button) { Icon = icon, Status = status };
        UpdateTile(tile, null);
        if (isConnected && action.Operation != QuickActionPolicy.Run)
            status.Text = "Loading…";
        button.Click += (_, _) => RequestAction(tile, null);
        button.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) => MoveTileFocus(tile, args)), handledEventsToo: true);
        return tile;
    }

    private ActionTile CreateLevelTile(QuickActionDefinition action)
    {
        var view = new QuickAccessLevelTile();
        view.UpdateLevel(null, _isConnected ? "Loading…" : "Unavailable");
        var tile = new ActionTile(action, view, view.Input) { LevelTile = view };
        view.LevelChanged += level =>
        {
            tile.PendingLevel = level;
            if (!tile.PointerDown) ScheduleLevelCommit();
        };
        view.Input.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => tile.PointerDown = true), handledEventsToo: true);
        view.Input.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FinishPointer), handledEventsToo: true);
        view.Input.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FinishPointer), handledEventsToo: true);
        return tile;

        void FinishPointer(object sender, PointerRoutedEventArgs args)
        {
            tile.PointerDown = false;
            ScheduleLevelCommit();
        }
    }

    private void ScheduleLevelCommit()
    {
        _levelCommitTimer.Stop();
        if (_tiles.Any(tile => tile.PendingLevel is not null && !tile.PointerDown)) _levelCommitTimer.Start();
    }

    private void CommitPendingLevel()
    {
        if (_pendingAction is not null || !_isConnected) return;
        var tile = _tiles.FirstOrDefault(tile => tile.PendingLevel is not null && !tile.PointerDown);
        if (tile?.PendingLevel is not { } level) return;
        tile.PendingLevel = null;
        if (tile.Input.IsEnabled && level != tile.State?.LevelFor(tile.Action.Operation))
            RequestAction(tile, level);
        else
            ScheduleLevelCommit();
    }

    private void RequestAction(ActionTile tile, int? level)
    {
        if (_pendingAction is not null) return;
        _pendingAction = tile;
        tile.PreviousState = tile.State;
        if (QuickActionState.Predict(tile.Action, tile.State, level) is { } predicted)
        {
            UpdateTile(tile, predicted);
            tile.IsOptimistic = true;
        }
        ActionRequested?.Invoke(tile.Action, level);
    }

    private void MoveTileFocus(ActionTile tile, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or
            Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        args.Handled = true;
        var enabled = _tiles.Where(candidate => candidate.Input.IsEnabled);
        var next = args.Key switch
        {
            Windows.System.VirtualKey.Left => enabled.FirstOrDefault(candidate => candidate.Row == tile.Row && candidate.Column < tile.Column),
            Windows.System.VirtualKey.Right => enabled.FirstOrDefault(candidate => candidate.Row == tile.Row && candidate.Column > tile.Column),
            Windows.System.VirtualKey.Up => NearestRow(-1),
            Windows.System.VirtualKey.Down => NearestRow(1),
            _ => null
        };
        next?.Input.Focus(FocusState.Keyboard);

        ActionTile? NearestRow(int direction) => enabled
            .Where(candidate => (candidate.Row - tile.Row) * direction > 0)
            .OrderBy(candidate => Math.Abs(candidate.Row - tile.Row))
            .ThenBy(candidate => Math.Abs(candidate.Column - tile.Column))
            .FirstOrDefault();
    }

    internal void CompleteAction()
    {
        _pendingAction = null;
        SetActionsEnabled(_isConnected);
        ConnectionMessage.Visibility = _isConnected ? Visibility.Collapsed : Visibility.Visible;
        ScheduleLevelCommit();
    }

    internal void SetEntityStates(IReadOnlyDictionary<string, QuickActionState>? states, bool preserveOptimistic = false)
    {
        if (!IsVisible || _pendingAction is not null) return;
        foreach (var tile in _tiles)
        {
            if (tile.PendingLevel is not null || tile.PointerDown) continue;
            if (states is null)
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
                continue;
            }

            var state = states.GetValueOrDefault(tile.Action.EntityId);
            if (preserveOptimistic && tile.IsOptimistic && SameActionState(tile, state))
                continue;
            tile.IsOptimistic = false;
            UpdateTile(tile, state);
        }
        SetActionsEnabled(_isConnected);
    }

    private static bool SameActionState(ActionTile tile, QuickActionState? state) => tile.LevelTile is not null
        ? state?.LevelFor(tile.Action.Operation) == tile.PreviousState?.LevelFor(tile.Action.Operation)
        : state?.State == tile.PreviousState?.State;

    private static void UpdateTile(ActionTile tile, QuickActionState? reading)
    {
        tile.State = reading;
        if (tile.LevelTile is { } level)
        {
            level.UpdateLevel(reading?.LevelFor(tile.Action.Operation), "Unavailable");
            return;
        }

        var status = QuickActionLabels.Status(tile.Action, reading);
        tile.Status!.Text = status;
        tile.Icon!.Foreground = reading?.State is "on" or "open" or "opening" ? AccentBrush : PrimaryBrush;
        var description = $"{QuickActionLabels.Operation(tile.Action.Operation)} {tile.Action.Label}. {status}.";
        AutomationProperties.SetName(tile.Input, description);
        ToolTipService.SetToolTip(tile.Input, $"{description} {tile.Action.EntityId}");
    }

    private static Brush? AccentBrush => Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var value)
        ? value as Brush : null;

    private static Brush? PrimaryBrush => Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var value)
        ? value as Brush : null;

    private static Brush? SecondaryBrush => Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var value)
        ? value as Brush : null;

    private sealed class ActionTile(QuickActionDefinition action, FrameworkElement element, Control input)
    {
        public QuickActionDefinition Action { get; } = action;
        public FrameworkElement Element { get; } = element;
        public Control Input { get; } = input;
        public IconSourceElement? Icon { get; init; }
        public TextBlock? Status { get; init; }
        public QuickAccessLevelTile? LevelTile { get; init; }
        public int Row => Grid.GetRow(Element);
        public int Column => Grid.GetColumn(Element);
        public QuickActionState? State { get; set; }
        public QuickActionState? PreviousState { get; set; }
        public bool IsOptimistic { get; set; }
        public int? PendingLevel { get; set; }
        public bool PointerDown { get; set; }
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var tile in _tiles)
            tile.Input.IsEnabled = enabled && (tile.LevelTile is not null
                ? tile.State?.LevelFor(tile.Action.Operation) is not null
                : tile.State?.State != "unavailable");
    }

    private void PositionNearCursor(bool hasMessage)
    {
        if (!GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, 2); // Nearest monitor.
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.WorkArea;
        var (width, height) = ResizeForMonitor(monitor, work, hasMessage);

        var x = Math.Clamp(cursor.X - width + 24, work.Left + EdgeGap, work.Right - width - EdgeGap);
        var above = cursor.Y - work.Top;
        var below = work.Bottom - cursor.Y;
        var y = below >= height + 12 && (below >= above || above < height + 12)
            ? cursor.Y + 12
            : cursor.Y - height - 12;
        y = Math.Clamp(y, work.Top + EdgeGap, work.Bottom - height - EdgeGap);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void PositionForShortcut(bool hasMessage)
    {
        var foreground = GetForegroundWindow();
        var monitor = foreground != 0 ? MonitorFromWindow(foreground, 2) : 0;
        if (monitor == 0 && GetCursorPos(out var cursor)) monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.WorkArea;
        var (width, height) = ResizeForMonitor(monitor, work, hasMessage);
        var x = work.Left + (work.Right - work.Left - width) / 2;
        var y = work.Top + (work.Bottom - work.Top - height) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private (int Width, int Height) ResizeForMonitor(nint monitor, NativeRect work, bool hasMessage)
    {
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0
            ? dpi / 96d
            : GetDpiForWindow(_windowHandle) / 96d;
        var rows = ActionsGrid.RowDefinitions.Count;
        var levelRows = _tiles.Where(tile => tile.LevelTile is not null).Select(tile => tile.Row).Distinct().Count();
        var contentHeight = levelRows == 0 ? 112 + rows * 72 : 96 + (rows - levelRows) * 72 + levelRows * 78;
        var desiredHeight = (rows == 0 ? 210 : contentHeight) + (hasMessage ? 26 : 0);
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
