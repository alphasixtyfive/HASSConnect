using HassConnect.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HassConnect.App.Views;

public sealed partial class NotificationSettingsView : UserControl
{
    private bool _busy;
    private bool _supported;
    private bool _registered;
    private CancellationTokenSource? _testFeedback;
    private bool _acceptedFeedback;

    public event Action<bool>? EnabledChanged;
    public event Action<bool>? SoundChanged;
    public event Action? TestRequested;

    public NotificationSettingsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => ClearTransientFeedback();
    }

    public void Update(Settings settings, bool supported, bool registered, bool connected, string status)
    {
        _supported = supported;
        _registered = registered;
        if (!_busy)
        {
            MasterRow.IsOn = settings.NotificationsEnabled;
            SoundRow.IsOn = settings.NotificationSound;
        }
        StatusText.Text = !supported ? status : !registered ? "Connect to Home Assistant in Settings." :
            settings.NotificationsEnabled && (!connected || status != "Connected") ? status : "";
        StatusText.Visibility = string.IsNullOrWhiteSpace(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshEnabledState();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshEnabledState();
    }

    public void BeginTest()
    {
        ClearTransientFeedback();
        TestRow.ValueText = "";
        ToolTipService.SetToolTip(TestRow, null);
        TestButton.Content = "Sending…";
    }

    public void ShowTestResult(bool accepted)
    {
        ClearTransientFeedback();
        _acceptedFeedback = accepted;
        TestButton.Content = "Send test";
        TestRow.ValueText = accepted ? "Sent to Windows" : "Failed";
        ToolTipService.SetToolTip(TestRow, accepted
            ? "Windows accepted the notification. If no banner appears, check notification center (Win+N) and Windows notification settings."
            : "The test failed. See the error message above.");
        if (!accepted) return;
        if (!IsLoaded || Visibility != Visibility.Visible)
        {
            ClearTransientFeedback();
            return;
        }
        var delay = new CancellationTokenSource();
        _testFeedback = delay;
        _ = ClearFeedbackAfterDelayAsync(delay, delay.Token);
    }

    public void ClearTransientFeedback()
    {
        _testFeedback?.Cancel();
        _testFeedback?.Dispose();
        _testFeedback = null;
        if (!_acceptedFeedback) return;
        _acceptedFeedback = false;
        TestRow.ValueText = "";
        ToolTipService.SetToolTip(TestRow, null);
    }

    private async Task ClearFeedbackAfterDelayAsync(CancellationTokenSource delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            if (ReferenceEquals(_testFeedback, delay)) ClearTransientFeedback();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RefreshEnabledState()
    {
        MasterRow.IsToggleEnabled = !_busy && (MasterRow.IsOn || (_supported && _registered));
        SoundRow.IsToggleEnabled = !_busy && _supported && MasterRow.IsOn;
        SoundRow.Opacity = _supported && MasterRow.IsOn ? 1 : 0.55;
        TestButton.IsEnabled = !_busy && _supported;
    }

    private void Master_Changed(object? sender, EventArgs args) => EnabledChanged?.Invoke(MasterRow.IsOn);
    private void Sound_Changed(object? sender, EventArgs args) => SoundChanged?.Invoke(SoundRow.IsOn);
    private void Test_Click(object sender, RoutedEventArgs args) => TestRequested?.Invoke();
}
