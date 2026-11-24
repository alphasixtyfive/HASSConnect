using HassConnect.App.Views;
using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HassConnect.App;

public sealed partial class CameraPreviewWindow
{
    private QuickActionDefinition[] _cameraActions = [];
    private ContentDialog? _cameraActionDialog;
    private bool _cameraActionBusy;

    internal void RefreshCameraActions()
    {
        if (_request is not { } request) return;
        var actions = _session.Settings.QuickActions.Where(action => action.CameraEntityId == request.EntityId).ToArray();
        if (!_cameraActions.SequenceEqual(actions))
        {
            _cameraActions = actions;
            CameraActionControls.Children.Clear();
            if (actions is [var action])
            {
                var content = new Grid { ColumnSpacing = 8 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.Children.Add(ActionIcon(action));
                var label = new TextBlock
                {
                    Text = action.Label,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                content.Children.Add(label);
                var button = new Button
                {
                    Content = content,
                    MaxWidth = 160,
                    MinHeight = 36
                };
                AutomationProperties.SetName(button, action.Label);
                ToolTipService.SetToolTip(button, action.Label);
                button.Click += async (_, _) => await InvokeCameraActionAsync(action);
                CameraActionControls.Children.Add(button);
            }
            else if (actions.Length > 1)
            {
                var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
                foreach (var item in actions)
                {
                    var button = new MenuFlyoutItem { Text = item.Label, Icon = ActionIcon(item) };
                    button.Click += async (_, _) => await InvokeCameraActionAsync(item);
                    menu.Items.Add(button);
                }
                CameraActionControls.Children.Add(new DropDownButton { Content = "Actions", MinHeight = 36, Flyout = menu });
            }
            CameraActionOverlay.Visibility = actions.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateCameraActionAvailability();
    }

    private static IconSourceElement ActionIcon(QuickActionDefinition action) => new()
    {
        IconSource = QuickActionIcons.SourceFor(action.Icon, action.EntityId, 16),
        Width = 16,
        Height = 16,
        VerticalAlignment = VerticalAlignment.Center
    };

    private bool CanUseCameraActions => _isLive && _session.Connected;

    private void UpdateCameraActionAvailability()
    {
        if (CameraActionControls.Children.FirstOrDefault() is Control control)
            control.IsEnabled = CanUseCameraActions && !_cameraActionBusy;
    }

    private async Task InvokeCameraActionAsync(QuickActionDefinition action)
    {
        bool CanRun() => CanUseCameraActions && _request?.EntityId == action.CameraEntityId && _cameraActions.Contains(action);

        if (_cameraActionBusy || !CanRun()) return;
        _cameraActionBusy = true;
        CameraActionFeedback.IsOpen = false;
        UpdateCameraActionAvailability();
        try
        {
            if (action.EntityId.StartsWith("cover.", StringComparison.Ordinal))
            {
                _cameraActionDialog = new ContentDialog
                {
                    XamlRoot = Surface.XamlRoot,
                    Title = $"{action.Label}?",
                    Content = "Send this command to Home Assistant.",
                    PrimaryButtonText = action.Label,
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await _cameraActionDialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            if (!CanRun()) return;
            await _session.InvokeQuickActionAsync(action, percentage: null);
            ShowCameraActionFeedback(action, "Command sent", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowCameraActionFeedback(action, MainWindow.UserMessage(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _cameraActionDialog = null;
            _cameraActionBusy = false;
            UpdateCameraActionAvailability();
        }
    }

    private void ShowCameraActionFeedback(QuickActionDefinition action, string message, InfoBarSeverity severity)
    {
        if (_request?.EntityId != action.CameraEntityId) return;
        CameraActionFeedback.Title = action.Label;
        CameraActionFeedback.Message = message;
        CameraActionFeedback.Severity = severity;
        CameraActionFeedback.IsOpen = true;
    }
}
