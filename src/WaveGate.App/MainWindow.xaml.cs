using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using WaveGate.Overlay.Config;

namespace WaveGate.Overlay;

/// <summary>
/// The compact always-on-top overlay showing Favorites or one category.
/// The window never takes focus, so clicking a clip does not pull you out of a game.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IBoard _board;
    private readonly ListCollectionView _view;

    public event Action? HideClicked;

    public MainWindow(IBoard board)
    {
        InitializeComponent();
        _board = board;

        _view = new ListCollectionView(board.Clips) { Filter = Matches };
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipEntry.Name), ListSortDirection.Ascending));
        ClipList.ItemsSource = _view;

        foreach (var clip in board.Clips) clip.PropertyChanged += OnClipChanged;
        board.Clips.CollectionChanged += OnClipsChanged;
        Refresh();
    }

    public void SetMicStatus(MicStatus status) => (MicDot.Fill, MicDot.ToolTip) = MicStatusText.Describe(status);

    /// <summary>Call after the board's overlay source or categories change.</summary>
    public void Refresh()
    {
        // A deleted or renamed category falls back to Favorites.
        if (_board.OverlaySource is { Length: > 0 } source &&
            !_board.Categories.Contains(source, StringComparer.OrdinalIgnoreCase))
            _board.OverlaySource = null;

        _view.Refresh();
        SourceTitle.Text = _board.OverlaySource switch
        {
            null => "★ Favorites",
            "" => "All sounds",
            var name => name,
        };
        EmptyHint.Text = _board.Clips.Count == 0 ? "Drop audio or video files here"
            : _board.OverlaySource is null ? "Star sounds in the Library to show them here"
            : "Nothing in this category yet";
        EmptyHint.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        // NOACTIVATE: clicks never steal focus. TOOLWINDOW: hidden from Alt+Tab.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private bool Matches(object item) => item is ClipEntry clip && _board.OverlaySource switch
    {
        null => clip.Favorite,
        "" => true,
        var name => string.Equals(clip.Category, name, StringComparison.OrdinalIgnoreCase),
    };

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (ClipEntry clip in e.NewItems ?? Array.Empty<ClipEntry>()) clip.PropertyChanged += OnClipChanged;
        foreach (ClipEntry clip in e.OldItems ?? Array.Empty<ClipEntry>()) clip.PropertyChanged -= OnClipChanged;
        Refresh();
    }

    private void OnClipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipEntry.Category) or nameof(ClipEntry.Favorite) or nameof(ClipEntry.Name))
            Refresh();
    }

    private void OnHeaderContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ((FrameworkElement)sender).ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Show in overlay", IsEnabled = false });

        void Add(string label, string? source)
        {
            var item = new MenuItem { Header = label, IsChecked = _board.OverlaySource == source };
            item.Click += (_, _) =>
            {
                _board.OverlaySource = source;
                Refresh();
            };
            menu.Items.Add(item);
        }

        Add("★ Favorites", null);
        Add("All sounds", "");
        foreach (var name in _board.Categories) Add(name, name);
    }

    private static ClipEntry? ClipOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipEntry;

    private void OnClipClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.Play(clip);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.Edit(clip);
    }

    private void OnSetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.SetHotkey(clip);
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.ClearHotkey(clip);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.Remove(clip);
    }

    private void OnLibraryClick(object sender, RoutedEventArgs e) => _board.ShowLibrary();

    private void OnStopAllClick(object sender, RoutedEventArgs e) => _board.StopAll();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideClicked?.Invoke();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files?.Any(f => Directory.Exists(f) || ConfigStore.IsSupported(f)) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        // Land the new clips where the overlay will show them.
        var category = _board.OverlaySource is { Length: > 0 } name ? name : null;
        _board.Import(files, category, favorite: _board.OverlaySource is null);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
