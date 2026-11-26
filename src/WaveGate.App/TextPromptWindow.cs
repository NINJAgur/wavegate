using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaveGate.Overlay;

/// <summary>Small modal dialog asking for one line of text.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;

    private TextPromptWindow(string title, string label, string initial)
    {
        WindowTheme.ApplyDark(this);
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("BgBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 14) };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };

        var ok = new Button { Content = "OK", Style = (Style)FindResource("AccentButton"), MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("WgButton"), MinWidth = 80,
                                  Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_box.Text)) return;
        DialogResult = true;
    }

    /// <summary>Returns the trimmed text, or null if cancelled.</summary>
    public static string? Ask(Window? owner, string title, string label, string initial)
    {
        var dialog = new TextPromptWindow(title, label, initial);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog._box.Text.Trim() : null;
    }
}
