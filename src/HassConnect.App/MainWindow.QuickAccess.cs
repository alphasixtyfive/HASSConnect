using System.ComponentModel;
using HassConnect.Core;
using HassConnect.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HassConnect.App;

public sealed partial class MainWindow
{
    private QuickAccessWindow? _quickAccessWindow;
    private QuickEntityChoice[] _quickEntities = [];
    private DispatcherQueueTimer? _quickSearchTimer;
    private string? _selectedQuickEntityId;
    private string? _suggestedQuickLabel;
    private int? _editingQuickIndex;
    private string? _editingCameraEntityId;
    private bool _editingCameraTile;
    private bool _quickDialogOpen;
    private bool _browseQuickEntities;
    private bool _quickSaving;
    private bool _quickShortcutSaving;
    private int _quickStateRequest;
    private int _quickInteractionGeneration;
    private CancellationTokenSource? _quickStateCancellation;

    private void InitializeQuickAccess()
    {
        _quickSearchTimer = DispatcherQueue.CreateTimer();
        _quickSearchTimer.Interval = TimeSpan.FromMilliseconds(180);
        _quickSearchTimer.IsRepeating = false;
        _quickSearchTimer.Tick += (_, _) => FilterQuickEntities();
        QuickAccessPage.AddRequested += () => _ = ShowQuickActionDialogAsync(null);
        QuickAccessPage.EditRequested += index => _ = EditQuickActionAsync(index);
        QuickAccessPage.RemoveRequested += index => _ = RemoveQuickActionAsync(index);
        QuickAccessPage.MoveRequested += (index, direction) => _ = MoveQuickActionAsync(index, direction);
        QuickAccessPage.ShortcutChanged += shortcut => _ = SetQuickAccessShortcutAsync(shortcut);
        if (!_tray.SetQuickAccessShortcut(_session?.Settings.QuickAccessShortcut))
            QuickAccessPage.SetShortcutStatus("Saved shortcut is in use. Choose another.");

        _quickAccessWindow = new QuickAccessWindow();
        _quickAccessWindow.Hidden += () => _quickStateCancellation?.Cancel();
        _quickAccessWindow.ActionRequested += (action, level) => _ = InvokeQuickActionAsync(action, level);
        _quickAccessWindow.EditRequested += () =>
        {
            ShowWindow();
            SelectPage("quick-access");
        };
        _quickAccessWindow.OpenHomeAssistantRequested += OpenHomeAssistant;
    }

    private void ShowQuickAccess(bool fromShortcut)
    {
        _quickInteractionGeneration++;
        if (_quickAccessWindow is null || _session is null) { ShowWindow(); return; }
        if (_quickAccessWindow.IsVisible) { _quickAccessWindow.Hide(); _quickStateRequest++; return; }
        _quickAccessWindow.SetTheme(Root.ActualTheme);
        var tiles = _session.Settings.QuickActions.Where(action => action.CameraEntityId is null).ToArray();
        if (fromShortcut)
            _quickAccessWindow.ShowFromShortcut(tiles, _session.Connected);
        else
            _quickAccessWindow.ShowNearTray(tiles, _session.Connected);
        if (_session.Connected && tiles.Length > 0)
            _ = RefreshQuickActionStatesAsync();
    }

    private async Task SetQuickAccessShortcutAsync(QuickAccessShortcut? shortcut)
    {
        if (_session is null || _quickShortcutSaving) return;
        if (!_tray.SetQuickAccessShortcut(shortcut))
        {
            QuickAccessPage.SetShortcutStatus("Shortcut is in use. Choose another.");
            return;
        }

        _quickShortcutSaving = true;
        var previous = _session.Settings.QuickAccessShortcut;
        try
        {
            await _session.SetQuickAccessShortcutAsync(shortcut);
            QuickAccessPage.SetShortcutStatus(null);
            Refresh();
        }
        catch (Exception ex)
        {
            _tray.SetQuickAccessShortcut(previous);
            QuickAccessPage.SetShortcutStatus(UserMessage(ex));
        }
        finally { _quickShortcutSaving = false; }
    }

    private async Task InvokeQuickActionAsync(QuickActionDefinition action, int? level)
    {
        if (_session is null || _quickAccessWindow is null) return;
        var generation = ++_quickInteractionGeneration;
        _quickStateCancellation?.Cancel();
        _quickStateRequest++; // Ignore any state request started before this command.
        try
        {
            if (action.Operation == QuickActionPolicy.ViewCamera)
            {
                if (OpenLiveCamera(new(action.EntityId, action.Label))) _quickAccessWindow.Hide();
                else _quickAccessWindow.ShowError("Connect to Home Assistant first.");
                return;
            }
            await _session.InvokeQuickActionAsync(action, level);
            if (!IsCurrentQuickInteraction(generation)) return;
            _quickAccessWindow.CompleteAction();
            await Task.Delay(700);
            if (!IsCurrentQuickInteraction(generation)) return;
            await RefreshQuickActionStatesAsync(preserveOptimistic: true);
            if (action.EntityId.Split('.')[0] is not ("script" or "scene" or "button"))
            {
                await Task.Delay(1500);
                if (IsCurrentQuickInteraction(generation)) await RefreshQuickActionStatesAsync();
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentQuickInteraction(generation)) _quickAccessWindow.ShowError(UserMessage(ex));
        }
    }

    private bool IsCurrentQuickInteraction(int generation) =>
        generation == _quickInteractionGeneration && _quickAccessWindow?.IsVisible == true;

    private async Task RefreshQuickActionStatesAsync(bool preserveOptimistic = false)
    {
        if (_session is null || _quickAccessWindow is null) return;
        var request = ++_quickStateRequest;
        var generation = _quickInteractionGeneration;
        _quickStateCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _quickStateCancellation = cancellation;
        try
        {
            var states = await _session.GetQuickActionStatesAsync(
                _session.Settings.QuickActions.Where(action => action.CameraEntityId is null && action.Operation != QuickActionPolicy.ViewCamera)
                    .Select(action => action.EntityId).Distinct().ToArray(), cancellation.Token);
            if (request == _quickStateRequest && IsCurrentQuickInteraction(generation))
                _quickAccessWindow.SetEntityStates(states, preserveOptimistic);
        }
        catch
        {
            if (request == _quickStateRequest && IsCurrentQuickInteraction(generation))
                _quickAccessWindow.SetEntityStates(null, preserveOptimistic);
        }
        finally
        {
            if (ReferenceEquals(_quickStateCancellation, cancellation)) _quickStateCancellation = null;
        }
    }

    private async Task MoveQuickActionAsync(int index, int direction)
    {
        if (_session is null) return;
        try { await _session.MoveQuickActionAsync(index, direction); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { Refresh(); }
    }

    private async Task RemoveQuickActionAsync(int index)
    {
        if (_session is null || index < 0 || index >= _session.Settings.QuickActions.Count) return;
        var action = _session.Settings.QuickActions[index];
        var hasCameraActions = action.Operation == QuickActionPolicy.ViewCamera &&
            _session.Settings.QuickActions.Any(item => item.CameraEntityId == action.EntityId);
        var message = hasCameraActions ? $"Remove “{action.Label}” and its camera actions?" : $"Remove “{action.Label}”?";
        var confirmed = await Views.DialogLayout.ShowConfirmationAsync(
            Root.XamlRoot, "Remove action?", message,
            "Remove", destructive: true);
        if (!confirmed) return;
        try { await _session.RemoveQuickActionAsync(index); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { Refresh(); }
    }

    private async Task<CameraActionEdit?> ShowQuickActionDialogAsync(int? index, QuickActionDefinition? camera = null)
    {
        if (_session is null || _quickDialogOpen) return null;
        if (index is { } position && (position < 0 || position >= _session.Settings.QuickActions.Count)) return null;
        _quickDialogOpen = true;
        using var loadCancellation = new CancellationTokenSource();
        CameraActionEdit? cameraAction = null;
        void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => loadCancellation.Cancel();
        QuickActionDialog.Closed += OnDialogClosed;
        try
        {
            _quickSearchTimer?.Stop();
            _editingQuickIndex = index;
            _editingCameraEntityId = camera?.EntityId;
            _quickEntities = [];
            var existing = index is { } i ? _session.Settings.QuickActions[i] : null;
            _editingCameraTile = existing?.Operation == QuickActionPolicy.ViewCamera;
            _selectedQuickEntityId = existing?.EntityId;
            _browseQuickEntities = existing is null;
            _suggestedQuickLabel = null;
            QuickActionDialog.XamlRoot = Root.XamlRoot;
            QuickActionDialog.Title = _editingCameraTile ? "Edit camera tile" : camera is null
                ? existing is null ? "Add tile" : "Edit tile"
                : existing is null ? "Add camera action" : "Edit camera action";
            SaveQuickActionButton.Content = existing is null ? "Add" : "Save";
            QuickActionError.Visibility = Visibility.Collapsed;
            QuickEntityStatus.Text = "Loading entities…";
            QuickEntityStatus.Visibility = Visibility.Visible;
            QuickEntitySearch.IsEnabled = QuickEntityTypeBox.IsEnabled = false;
            QuickActionLabelBox.Text = existing?.Label ?? "";
            QuickActionSizeBox.SelectedIndex = (int)(existing?.Size ?? QuickActionSize.Auto);
            QuickActionSizeOptions.Visibility = camera is null ? Visibility.Visible : Visibility.Collapsed;
            QuickTileSettingsBackground.Visibility = _editingCameraTile ? Visibility.Visible : Visibility.Collapsed;
            QuickTileSettingsFields.Margin = _editingCameraTile ? new Thickness(12) : new Thickness(0);
            SelectedCameraRow.Visibility = _editingCameraTile ? Visibility.Visible : Visibility.Collapsed;
            SelectedCameraEntity.Text = _editingCameraTile ? _selectedQuickEntityId ?? "" : "";
            SetQuickEntityBrowser(open: !_editingCameraTile);
            QuickEntityTypeLabel.Visibility = QuickEntityTypeBox.Visibility = _editingCameraTile ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(QuickEntitySearch, _editingCameraTile ? 2 : 1);
            QuickActionOperationOptions.Visibility = _editingCameraTile ? Visibility.Collapsed : Visibility.Visible;
            QuickActionOperationColumn.Width = _editingCameraTile ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            QuickActionIconPicker.Margin = _editingCameraTile ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            QuickEntitySearch.Text = "";
            QuickEntityTypeBox.SelectedIndex = 0;
            QuickEntityList.ItemsSource = null;
            QuickEntityList.Visibility = Visibility.Collapsed;
            QuickActionIconPicker.SelectIcon(existing?.Icon);
            QuickActionIconPicker.SetEntity(existing?.EntityId, null);
            SetQuickOperations(null, existing?.Operation);
            SetCameraActionEditor(existing, selection =>
            {
                if (_quickSaving) return;
                cameraAction = selection;
                QuickActionDialog.Hide();
            });

            var dialog = QuickActionDialog.ShowAsync();
            try
            {
                var entities = await _session.GetQuickActionEntitiesAsync(loadCancellation.Token);
                if (loadCancellation.IsCancellationRequested) return cameraAction;
                _quickEntities = entities
                    .Where(entity => !_editingCameraTile || CameraEntityId.IsValid(entity.EntityId))
                    .Where(entity => camera is null || entity.Operations.Any(QuickActionPolicy.CanShowOnCamera))
                    .Select(entity => new QuickEntityChoice(entity)).ToArray();
                QuickEntitySearch.IsEnabled = QuickEntityTypeBox.IsEnabled = _quickEntities.Length > 0;
                FilterQuickEntities(preserveSelection: true);
            }
            catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested) { return cameraAction; }
            catch (Exception ex)
            {
                if (loadCancellation.IsCancellationRequested) return cameraAction;
                QuickEntityStatus.Text = existing is null ? UserMessage(ex) :
                    "Entity list unavailable. You can still edit this tile.";
                QuickEntityStatus.Visibility = Visibility.Visible;
            }
            await dialog;
            return cameraAction;
        }
        finally
        {
            QuickActionDialog.Closed -= OnDialogClosed;
            loadCancellation.Cancel();
            _quickSearchTimer?.Stop();
            _quickDialogOpen = false;
            _editingQuickIndex = null;
            _editingCameraEntityId = null;
            _editingCameraTile = false;
            _selectedQuickEntityId = null;
            _suggestedQuickLabel = null;
            _quickEntities = [];
        }
    }

    private void SetQuickEntityBrowser(bool open)
    {
        QuickEntityBrowser.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChangeCameraButton.Content = open ? "Done" : "Change";
    }

    private void ChangeCamera_Click(object sender, RoutedEventArgs args)
    {
        var open = QuickEntityBrowser.Visibility != Visibility.Visible;
        SetQuickEntityBrowser(open);
        if (!open) return;
        _browseQuickEntities = true;
        if (_quickEntities.Length > 0) FilterQuickEntities(preserveSelection: true);
        QuickEntitySearch.Focus(FocusState.Programmatic);
    }

    private List<QuickEntityChoice> FilterQuickEntities(bool preserveSelection = false)
    {
        var search = QuickEntitySearch.Text.Trim();
        var domain = (QuickEntityTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var matches = new List<QuickEntityChoice>();
        foreach (var entity in _quickEntities)
        {
            if (!_browseQuickEntities && entity.EntityId != _selectedQuickEntityId ||
                domain.Length > 0 && entity.Domain != domain ||
                search.Length > 0 && !entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !entity.EntityId.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(entity);
        }
        QuickEntityList.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QuickEntityList.ItemsSource = matches;
        QuickEntityList.SelectedItem = matches.FirstOrDefault(entity => entity.EntityId == _selectedQuickEntityId);
        if (QuickEntityList.SelectedItem is null && !preserveSelection && !_editingCameraTile)
        {
            _selectedQuickEntityId = null;
            QuickActionIconPicker.SetEntity(null, null);
            SetQuickOperations(null, null);
        }
        QuickEntityStatus.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count == 0)
            QuickEntityStatus.Text = !_browseQuickEntities ? $"Saved entity unavailable · {_selectedQuickEntityId}" : _quickEntities.Length == 0
                ? "No supported entities found." : "No matches. Try another name or type.";
        return matches;
    }

    private void QuickEntitySearch_GotFocus(object sender, RoutedEventArgs args)
    {
        if (_browseQuickEntities || _quickEntities.Length == 0) return;
        _browseQuickEntities = true;
        FilterQuickEntities(preserveSelection: true);
    }

    private void QuickEntitySearch_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_quickEntities.Length == 0) return;
        _browseQuickEntities = true;
        _quickSearchTimer?.Stop();
        _quickSearchTimer?.Start();
    }

    private void QuickEntityType_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_quickEntities.Length == 0) return;
        _browseQuickEntities = true;
        _quickSearchTimer?.Stop();
        FilterQuickEntities();
    }

    private void QuickEntitySearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Enter)) return;
        _quickSearchTimer?.Stop();
        var matches = FilterQuickEntities();
        if (matches.Count == 0) return;
        QuickEntityList.SelectedItem ??= matches[0];
        if (args.Key == Windows.System.VirtualKey.Down) QuickEntityList.Focus(FocusState.Programmatic);
        else QuickActionLabelBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void QuickEntityList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (QuickEntityChoice removed in args.RemovedItems)
            removed.SeparatorOpacity = 0.35;
        foreach (QuickEntityChoice added in args.AddedItems)
            added.SeparatorOpacity = 0;
        if (QuickEntityList.SelectedItem is not QuickEntityChoice choice) return;
        var entity = choice.Entity;
        var changed = _selectedQuickEntityId != entity.EntityId;
        var original = _editingQuickIndex is { } index ? _session?.Settings.QuickActions[index] : null;
        var operation = _selectedQuickEntityId == entity.EntityId ? SelectedQuickOperation :
            original?.EntityId == entity.EntityId ? original.Operation : null;
        if (string.IsNullOrWhiteSpace(QuickActionLabelBox.Text) || QuickActionLabelBox.Text == _suggestedQuickLabel)
            QuickActionLabelBox.Text = entity.Name;
        _selectedQuickEntityId = entity.EntityId;
        _suggestedQuickLabel = entity.Name;
        QuickActionIconPicker.SetEntity(entity.EntityId, entity.Icon);
        SetQuickOperations(entity, operation);
        if (_editingCameraTile && changed)
        {
            SelectedCameraEntity.Text = entity.EntityId;
            SetQuickEntityBrowser(open: false);
        }
    }

    private void SetQuickOperations(QuickActionEntity? entity, string? selected)
    {
        QuickActionOperationBox.Items.Clear();
        // A saved action can be edited while its live capabilities are unavailable.
        var operations = entity?.Operations ?? (selected is null ? [] : [selected]);
        if (_editingCameraEntityId is not null)
            operations = operations.Where(QuickActionPolicy.CanShowOnCamera).ToArray();
        foreach (var operation in operations)
            QuickActionOperationBox.Items.Add(new ComboBoxItem
            {
                Content = QuickActionLabels.Operation(operation),
                Tag = operation
            });
        QuickActionOperationBox.IsEnabled = entity is not null && operations.Count > 0;
        QuickActionOperationBox.SelectedItem = selected is null
            ? QuickActionOperationBox.Items.FirstOrDefault()
            : QuickActionOperationBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == selected);
        QuickActionOperationBox.PlaceholderText = entity is not null && QuickActionOperationBox.Items.Count == 0
            ? "No actions" : selected is not null && QuickActionOperationBox.SelectedItem is null
                ? "Choose action" : "Choose entity";
        RefreshQuickActionSaveButton();
    }

    private void QuickActionOperation_Changed(object sender, SelectionChangedEventArgs args) => RefreshQuickActionSaveButton();

    private string? SelectedQuickOperation => (QuickActionOperationBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void RefreshQuickActionSaveButton() => SaveQuickActionButton.IsEnabled = !_quickSaving &&
        _selectedQuickEntityId is not null && QuickActionOperationBox.SelectedItem is not null;

    private async void SaveQuickAction_Click(object sender, RoutedEventArgs args)
    {
        if (_session is null || _quickSaving) return;
        _quickSaving = true;
        SaveQuickActionButton.IsEnabled = false;
        try
        {
            var icon = QuickActionIconPicker.SelectedIcon;
            if (string.IsNullOrEmpty(icon))
                icon = _quickEntities.FirstOrDefault(entity => entity.EntityId == _selectedQuickEntityId)?.Entity.Icon;
            var size = _editingCameraEntityId is null ? (QuickActionSize)QuickActionSizeBox.SelectedIndex : QuickActionSize.Auto;
            var action = QuickActionPolicy.Create(_selectedQuickEntityId, QuickActionLabelBox.Text, icon, SelectedQuickOperation, size,
                _editingCameraEntityId);
            await _session.SaveQuickActionAsync(action, _editingQuickIndex);
            QuickActionDialog.Hide();
            Refresh();
        }
        catch (Exception ex)
        {
            QuickActionError.Text = ex is InvalidDataException ? ex.Message : UserMessage(ex);
            QuickActionError.Visibility = Visibility.Visible;
        }
        finally { _quickSaving = false; RefreshQuickActionSaveButton(); }
    }

    private void CancelQuickAction_Click(object sender, RoutedEventArgs args)
    {
        if (!_quickSaving) QuickActionDialog.Hide();
    }
}

public sealed class QuickEntityChoice : INotifyPropertyChanged
{
    private double _separatorOpacity = 0.35;

    public QuickEntityChoice(QuickActionEntity entity)
    {
        Entity = entity;
        Glyph = QuickActionIcons.EntityGlyphFor(entity.Icon, entity.EntityId);
        Name = entity.Name;
        EntityId = entity.EntityId;
        Domain = EntityId[..EntityId.IndexOf('.')];
    }

    public QuickActionEntity Entity { get; }
    public string Glyph { get; }
    public string Name { get; }
    public string EntityId { get; }
    public string Domain { get; }

    public double SeparatorOpacity
    {
        get => _separatorOpacity;
        set
        {
            if (_separatorOpacity == value) return;
            _separatorOpacity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorOpacity)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
