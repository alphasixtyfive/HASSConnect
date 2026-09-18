using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HassConnect.App.Views;

internal static class DialogLayout
{
    public static async Task<bool> ShowConfirmationAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryText,
        bool defaultToPrimary,
        bool destructive = false)
    {
        var accepted = false;
        var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = title };
        var primary = new Button
        {
            Content = primaryText,
            Width = 120,
            Style = (Style)Application.Current.Resources[
                destructive ? "DangerButtonStyle" : "AccentButtonStyle"]
        };
        var cancel = new Button { Content = "Cancel", Width = 120 };
        primary.Click += (_, _) => { accepted = true; dialog.Hide(); };
        cancel.Click += (_, _) => dialog.Hide();
        dialog.KeyDown += (_, args) => CloseOnEscape(dialog, args);
        dialog.Opened += (_, _) => (defaultToPrimary ? primary : cancel).Focus(FocusState.Programmatic);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(primary);
        actions.Children.Add(cancel);
        var footer = new Border
        {
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SectionBorderBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 16, 0, 0),
            Margin = new Thickness(0, 4, 0, 0),
            Child = actions
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(footer);
        dialog.Content = content;

        await dialog.ShowAsync();
        return accepted;
    }

    public static void CloseOnEscape(ContentDialog dialog, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        dialog.Hide();
    }
}
