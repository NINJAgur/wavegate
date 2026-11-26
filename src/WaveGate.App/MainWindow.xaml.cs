using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WaveGate.Overlay.Config;

namespace WaveGate.Overlay;

public enum MicStatus { NotSetUp, Waiting, Live }

/// <summary>
/// The overlay itself. It only raises intents; App decides what they do.
/// The window never takes focus, so clicking a clip does not pull you out of a game.
/// </summary>
public partial class MainWindow : Window
{
    public event Action<ClipEntry>? ClipClicked;
    public event Action<ClipEntry>? SetHotkeyRequested;
    public event Action<ClipEntry>? ClearHotkeyRequested;
    public event Action<ClipEntry>? RemoveRequested;
    public event Action<string[]>? FilesDropped;
    public event Action? StopAllClicked;
    public event Action? HideClicked;

    public MainWindow(ObservableCollection<ClipEntry> clips)
    {
        InitializeComponent();
        ClipList.ItemsSource = clips;
        clips.CollectionChanged += (_, _) => UpdateEmptyHint(clips);
        UpdateEmptyHint(clips);
    }

    public void SetMicStatus(MicStatus status)
    {
        (MicDot.Fill, MicDot.ToolTip) = status switch
        {
            MicStatus.Live => ((Brush)new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), "Live: clips go into the microphone"),
            MicStatus.Waiting => (new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), "Waiting for an app to use the microphone"),
            _ => (new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), "No microphone set up (tray icon > Microphone)"),
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        // NOACTIVATE: clicks never steal focus. TOOLWINDOW: hidden from Alt+Tab.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private void UpdateEmptyHint(ObservableCollection<ClipEntry> clips) =>
        EmptyHint.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static ClipEntry? ClipOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipEntry;

    private void OnClipClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) ClipClicked?.Invoke(clip);
    }

    private void OnSetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) SetHotkeyRequested?.Invoke(clip);
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) ClearHotkeyRequested?.Invoke(clip);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) RemoveRequested?.Invoke(clip);
    }

    private void OnStopAllClick(object sender, RoutedEventArgs e) => StopAllClicked?.Invoke();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideClicked?.Invoke();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files?.Any(ConfigStore.IsSupported) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            FilesDropped?.Invoke(files.Where(ConfigStore.IsSupported).ToArray());
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
