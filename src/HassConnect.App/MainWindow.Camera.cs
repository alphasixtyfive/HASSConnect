using HassConnect.App.Services;
using HassConnect.App.Views;
using HassConnect.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App;

public sealed partial class MainWindow
{
    private sealed record CameraActionEdit(int? Index = null, bool Remove = false);

    private async Task EditQuickActionAsync(int index)
    {
        if (_session is not { } session || index < 0 || index >= session.Settings.QuickActions.Count) return;
        var tile = session.Settings.QuickActions[index];
        try
        {
            if (tile.Operation != QuickActionPolicy.ViewCamera)
            {
                await ShowQuickActionDialogAsync(index);
                return;
            }

            while (!_quitting)
            {
                var cameraIndex = session.Settings.QuickActions.ToList()
                    .FindIndex(action => action.Operation == QuickActionPolicy.ViewCamera && action.EntityId == tile.EntityId);
                if (cameraIndex < 0) return;
                var camera = session.Settings.QuickActions[cameraIndex];
                var next = await ShowQuickActionDialogAsync(cameraIndex);
                if (next is null) return;
                if (next is { Remove: true, Index: { } removeIndex })
                    await RemoveQuickActionAsync(removeIndex);
                else
                    await ShowQuickActionDialogAsync(next.Index, camera);
            }
        }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
    }

    private void SetCameraActionEditor(QuickActionDefinition? tile, Action<CameraActionEdit> select)
    {
        CameraActionEditor.Children.Clear();
        CameraActionEditor.Visibility = Visibility.Collapsed;
        if (tile is not { Operation: QuickActionPolicy.ViewCamera } || _session is not { } session) return;

        var actions = session.Settings.QuickActions;
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(12, 0, 12, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Camera actions",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var add = new Button { Content = "Add action", Height = 32, IsEnabled = actions.Count < QuickActionPolicy.MaximumActions };
        AutomationProperties.SetAutomationId(add, "AddCameraActionButton");
        add.Click += (_, _) => select(new());
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        CameraActionEditor.Children.Add(header);

        var list = new StackPanel { Spacing = 6 };
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action.CameraEntityId != tile.EntityId) continue;
            var index = i;
            var row = new Grid { ColumnSpacing = 10, MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new IconSourceElement
            {
                IconSource = QuickActionIcons.SourceFor(action.Icon, action.EntityId, 16),
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
            var label = new TextBlock { Text = action.Label, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            controls.Children.Add(QuickAccessSettingsView.CreateIconButton("\uE713", $"Edit {action.Label}", true,
                () => select(new(index))));
            controls.Children.Add(QuickAccessSettingsView.CreateIconButton("\uE74D", $"Remove {action.Label}", true,
                () => select(new(index, Remove: true))));
            Grid.SetColumn(controls, 2);
            row.Children.Add(controls);
            list.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(11, 4, 11, 4),
                Child = row
            });
        }
        if (list.Children.Count != 0)
            CameraActionEditor.Children.Add(new ScrollViewer { Content = list, MaxHeight = 112 });
        CameraActionEditor.Visibility = Visibility.Visible;
    }

    private Task<bool> OpenCameraFromNotificationAsync(CameraOpenRequest request, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var settings = _session?.Settings;
                completion.TrySetResult(!ct.IsCancellationRequested && settings?.NotificationsEnabled == true &&
                    (!request.Automatic || settings.CameraPopupsEnabled &&
                        (!settings.NotificationsOnlyWhenActive || _session?.IsUserActive() == true)) &&
                    OpenLiveCamera(request));
            }
            catch (Exception ex)
            {
                AppLog.Write("Open camera popup", ex);
                completion.TrySetResult(false);
            }
        })) return Task.FromResult(false);
        return completion.Task.WaitAsync(ct);
    }

    private bool OpenLiveCamera(CameraOpenRequest request)
    {
        if (_quitting || _session is not { IsRegistered: true } session) return false;
        var preview = GetCameraPreview(session, request.Automatic);
        preview.OpenCamera(request);
        return true;
    }

    private CameraPreviewWindow GetCameraPreview(SensorSession session, bool automatic)
    {
        if (_cameraPreview is { } current) return current;
        var preview = new CameraPreviewWindow(session, Root.RequestedTheme);
        var scale = Root.XamlRoot?.RasterizationScale ?? GetDpiForWindow(_windowHandle) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(664 * scale), workArea.Width);
        var height = Math.Min((int)(418 * scale), workArea.Height);
        var margin = (int)(20 * scale);
        var left = automatic ? Math.Max(0, workArea.Width - width - margin) : (workArea.Width - width) / 2;
        var top = automatic ? Math.Max(0, workArea.Height - height - margin) : (workArea.Height - height) / 2;
        preview.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(workArea.X + left, workArea.Y + top, width, height));
        _cameraPreview = preview;
        preview.Closed += (_, _) => _cameraPreview = null;
        return preview;
    }
}
