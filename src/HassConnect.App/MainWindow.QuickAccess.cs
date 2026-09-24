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
    private IReadOnlyList<QuickEntityChoice> _quickVisibleEntities = [];
    private DispatcherQueueTimer? _quickSearchTimer;
    private string? _selectedQuickEntityId;
    private string? _suggestedQuickLabel;
    private int? _editingQuickIndex;
    private bool _quickDialogOpen;
    private bool _quickSaving;
    private bool _quickShortcutSaving;
    private int _quickStateRequest;
    private int _quickInteractionGeneration;

    private void InitializeQuickAccess()
    {
        _quickSearchTimer = DispatcherQueue.CreateTimer();
        _quickSearchTimer.Interval = TimeSpan.FromMilliseconds(180);
        _quickSearchTimer.IsRepeating = false;
        _quickSearchTimer.Tick += (_, _) => FilterQuickEntities();
        QuickAccessPage.AddRequested += () => _ = ShowQuickActionDialogAsync(null);
        QuickAccessPage.EditRequested += index => _ = ShowQuickActionDialogAsync(index);
        QuickAccessPage.RemoveRequested += index => _ = RemoveQuickActionAsync(index);
        QuickAccessPage.MoveRequested += (index, direction) => _ = MoveQuickActionAsync(index, direction);
        QuickAccessPage.ShortcutChanged += shortcut => _ = SetQuickAccessShortcutAsync(shortcut);
        if (!_tray.SetQuickAccessShortcut(_session?.Settings.QuickAccessShortcut))
            QuickAccessPage.SetShortcutStatus("Saved shortcut is in use. Choose another.");

        _quickAccessWindow = new QuickAccessWindow();
        _quickAccessWindow.ActionRequested += action => _ = InvokeQuickActionAsync(action);
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
        if (fromShortcut)
            _quickAccessWindow.ShowFromShortcut(_session.Settings.QuickActions, _session.Connected);
        else
            _quickAccessWindow.ShowNearTray(_session.Settings.QuickActions, _session.Connected);
        if (_session.Connected && _session.Settings.QuickActions.Count > 0)
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

    private async Task InvokeQuickActionAsync(QuickActionDefinition action)
    {
        if (_session is null || _quickAccessWindow is null) return;
        var generation = ++_quickInteractionGeneration;
        _quickStateRequest++; // Ignore any state request started before this command.
        try
        {
            await _session.InvokeQuickActionAsync(action);
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
        try
        {
            var states = await _session.GetQuickActionStatesAsync(
                _session.Settings.QuickActions.Select(action => action.EntityId).Distinct().ToArray());
            if (request == _quickStateRequest && IsCurrentQuickInteraction(generation))
                _quickAccessWindow.SetEntityStates(states, preserveOptimistic);
        }
        catch
        {
            if (request == _quickStateRequest && IsCurrentQuickInteraction(generation))
                _quickAccessWindow.SetEntityStates(null, preserveOptimistic);
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
        var confirmed = await Views.DialogLayout.ShowConfirmationAsync(
            Root.XamlRoot, "Remove quick action?", $"Remove “{action.Label}” from Quick access?",
            "Remove", defaultToPrimary: false, destructive: true);
        if (!confirmed) return;
        try { await _session.RemoveQuickActionAsync(index); }
        catch (Exception ex) { ShowError(UserMessage(ex)); }
        finally { Refresh(); }
    }

    private async Task ShowQuickActionDialogAsync(int? index)
    {
        if (_session is null || _quickDialogOpen) return;
        if (index is { } position && (position < 0 || position >= _session.Settings.QuickActions.Count)) return;
        _quickDialogOpen = true;
        using var loadCancellation = new CancellationTokenSource();
        void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => loadCancellation.Cancel();
        QuickActionDialog.Closed += OnDialogClosed;
        try
        {
            _quickSearchTimer?.Stop();
            _editingQuickIndex = index;
            _quickEntities = [];
            _quickVisibleEntities = [];
            var existing = index is { } i ? _session.Settings.QuickActions[i] : null;
            _selectedQuickEntityId = existing?.EntityId;
            _suggestedQuickLabel = null;
            QuickActionDialog.XamlRoot = Root.XamlRoot;
            QuickActionDialog.Title = existing is null ? "Add quick action" : "Edit quick action";
            SaveQuickActionButton.Content = existing is null ? "Add" : "Save";
            SaveQuickActionButton.IsEnabled = false;
            QuickActionError.Visibility = Visibility.Collapsed;
            QuickEntityStatus.Text = "Loading entities…";
            QuickEntityStatus.Visibility = Visibility.Visible;
            QuickEntitySearch.IsEnabled = true;
            QuickActionLabelBox.Text = existing?.Label ?? "";
            QuickEntitySearch.Text = existing?.EntityId ?? "";
            QuickEntityTypeBox.SelectedIndex = 0;
            QuickEntityList.ItemsSource = null;
            QuickActionIconPicker.SelectIcon(existing?.Icon);
            QuickActionIconPicker.SetEntity(existing?.EntityId, null);
            SetQuickOperations(existing?.EntityId, existing?.Operation);

            var dialog = QuickActionDialog.ShowAsync();
            try
            {
                var entities = await _session.GetQuickActionEntitiesAsync(loadCancellation.Token);
                if (loadCancellation.IsCancellationRequested) return;
                _quickEntities = entities
                    .Select(entity => new QuickEntityChoice(entity)).ToArray();
                _quickSearchTimer?.Stop();
                FilterQuickEntities();
                SaveQuickActionButton.IsEnabled = _selectedQuickEntityId is not null &&
                    QuickActionOperationBox.SelectedItem is not null;
            }
            catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (loadCancellation.IsCancellationRequested) return;
                QuickActionError.Text = UserMessage(ex);
                QuickActionError.Visibility = Visibility.Visible;
                QuickEntityStatus.Text = "Entity list unavailable.";
                QuickEntityStatus.Visibility = Visibility.Visible;
                QuickEntitySearch.IsEnabled = false;
                // Keep the saved operation available for edits when the entity list is unavailable.
                if (existing is not null && QuickActionOperationBox.SelectedItem is null)
                {
                    QuickActionOperationBox.Items.Add(new ComboBoxItem
                    {
                        Content = QuickActionLabels.Operation(existing.Operation),
                        Tag = existing.Operation
                    });
                    QuickActionOperationBox.SelectedIndex = 0;
                }
                QuickActionOperationBox.IsEnabled = false;
                SaveQuickActionButton.IsEnabled = existing is not null;
            }
            await dialog;
        }
        finally
        {
            QuickActionDialog.Closed -= OnDialogClosed;
            loadCancellation.Cancel();
            _quickSearchTimer?.Stop();
            _quickDialogOpen = false;
            _editingQuickIndex = null;
            _selectedQuickEntityId = null;
            _suggestedQuickLabel = null;
            _quickEntities = [];
            _quickVisibleEntities = [];
        }
    }

    private void FilterQuickEntities()
    {
        var search = QuickEntitySearch.Text.Trim();
        var domain = (QuickEntityTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var matches = new List<QuickEntityChoice>();
        foreach (var entity in _quickEntities)
        {
            if (domain.Length > 0 && entity.Domain != domain ||
                search.Length > 0 && !entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !entity.EntityId.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(entity);
        }
        _quickVisibleEntities = matches;
        QuickEntityList.ItemsSource = matches;
        QuickEntityList.SelectedItem = matches.FirstOrDefault(entity => entity.EntityId == _selectedQuickEntityId);
        if (QuickEntityList.SelectedItem is null && _quickEntities.Length > 0)
        {
            _selectedQuickEntityId = null;
            QuickActionIconPicker.SetEntity(null, null);
            SetQuickOperations(null, null);
            SaveQuickActionButton.IsEnabled = false;
        }
        QuickEntityStatus.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count == 0)
            QuickEntityStatus.Text = _quickEntities.Length == 0
                ? "No supported entities found." : "No matches. Try another name or type.";
    }

    private void QuickEntitySearch_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_quickEntities.Length == 0) return;
        _selectedQuickEntityId = null;
        QuickEntityList.SelectedItem = null;
        QuickActionIconPicker.SetEntity(null, null);
        SetQuickOperations(null, null);
        SaveQuickActionButton.IsEnabled = false;
        _quickSearchTimer?.Stop();
        _quickSearchTimer?.Start();
    }

    private void QuickEntityType_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_quickEntities.Length == 0) return;
        _quickSearchTimer?.Stop();
        FilterQuickEntities();
    }

    private void QuickEntitySearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Enter)) return;
        _quickSearchTimer?.Stop();
        FilterQuickEntities();
        if (_quickVisibleEntities.Count == 0) return;
        QuickEntityList.SelectedItem ??= _quickVisibleEntities[0];
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
        if (string.IsNullOrWhiteSpace(QuickActionLabelBox.Text) || QuickActionLabelBox.Text == _suggestedQuickLabel)
            QuickActionLabelBox.Text = entity.Name;
        _selectedQuickEntityId = entity.EntityId;
        _suggestedQuickLabel = entity.Name;
        QuickActionIconPicker.SetEntity(entity.EntityId, entity.Icon);
        var original = _editingQuickIndex is { } index ? _session?.Settings.QuickActions[index] : null;
        SetQuickOperations(entity.EntityId, original?.EntityId == entity.EntityId ? original.Operation : null);
        SaveQuickActionButton.IsEnabled = QuickActionOperationBox.SelectedItem is not null;
    }

    private void SetQuickOperations(string? entityId, string? selected)
    {
        QuickActionOperationBox.Items.Clear();
        var features = _quickEntities.FirstOrDefault(entity => entity.EntityId == entityId)?.Entity.SupportedFeatures;
        var operations = entityId?.StartsWith("cover.", StringComparison.Ordinal) == true && features is null
            ? [] : QuickActionPolicy.AllowedOperations(entityId, features);
        foreach (var operation in operations)
            QuickActionOperationBox.Items.Add(new ComboBoxItem
            {
                Content = QuickActionLabels.Operation(operation),
                Tag = operation
            });
        QuickActionOperationBox.IsEnabled = QuickActionOperationBox.Items.Count > 0;
        QuickActionOperationBox.SelectedItem = selected is null
            ? QuickActionOperationBox.Items.FirstOrDefault()
            : QuickActionOperationBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == selected);
        QuickActionOperationBox.PlaceholderText = entityId is not null && QuickActionOperationBox.Items.Count == 0
            ? "No supported actions" : selected is not null && QuickActionOperationBox.SelectedItem is null
                ? "Choose a supported action" : "Select an entity first";
    }

    private async void SaveQuickAction_Click(object sender, RoutedEventArgs args)
    {
        if (_session is null || _quickSaving) return;
        _quickSaving = true;
        SaveQuickActionButton.IsEnabled = false;
        try
        {
            var operation = (QuickActionOperationBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var icon = QuickActionIconPicker.SelectedIcon;
            if (string.IsNullOrEmpty(icon))
                icon = _quickEntities.FirstOrDefault(entity => entity.EntityId == _selectedQuickEntityId)?.Entity.Icon;
            var action = QuickActionPolicy.Create(_selectedQuickEntityId, QuickActionLabelBox.Text, icon, operation);
            await _session.SaveQuickActionAsync(action, _editingQuickIndex);
            QuickActionDialog.Hide();
            Refresh();
        }
        catch (Exception ex)
        {
            QuickActionError.Text = ex is InvalidDataException ? ex.Message : UserMessage(ex);
            QuickActionError.Visibility = Visibility.Visible;
        }
        finally { _quickSaving = false; SaveQuickActionButton.IsEnabled = _selectedQuickEntityId is not null &&
            QuickActionOperationBox.SelectedItem is not null; }
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
        Glyph = QuickActionIcons.GlyphFor(entity.Icon, entity.EntityId);
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
