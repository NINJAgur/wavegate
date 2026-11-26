using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaveGate.Overlay.Hotkeys;

/// <summary>Small modal prompt: "press the combination you want".</summary>
public sealed class HotkeyCaptureWindow : Window
{
    private readonly TextBlock _prompt;

    public Hotkey? Result { get; private set; }

    public HotkeyCaptureWindow(string target)
    {
        Title = "Set hotkey";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        _prompt = new TextBlock
        {
            Text = $"Press a key combination for \"{target}\".\nEsc cancels.",
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
        };
        Content = _prompt;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => Activate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combos arrive as Key.System
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                   Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // wait for the actual key

        var hotkey = new Hotkey(modifiers, key);
        if (!hotkey.IsSafeGlobal)
        {
            _prompt.Text = "Add Ctrl, Alt or Win. Only F1-F24 and numpad keys may be used on their own.";
            return;
        }

        Result = hotkey;
        DialogResult = true;
    }
}
