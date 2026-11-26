using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    public event Action? ExitClicked;

    public MainWindow(IBoard board)
    {
        InitializeComponent();
        WindowTheme.FadeInOnShow(this);
        _board = board;

        _view = new ListCollectionView(board.Clips) { Filter = Matches };
        _view.SortDescriptions.Add(new SortDescription(nameof(ClipEntry.Name), ListSortDirection.Ascending));
        ClipList.ItemsSource = _view;
        ApplyOrientation();

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
            null => "Favorites",
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

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitClicked?.Invoke();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove(); // returns when the mouse is released
        SnapToEdge(animate: true, pickEdge: true);
    }

    // ---- Edge anchoring ----------------------------------------------------
    // The overlay always rests against an edge of the monitor it is on. Dragging moves it freely;
    // on release it glides to the nearest edge, reshaping its tiles to suit, and stays pinned as its size changes.

    private const double SnapMargin = 12;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        SnapToEdge(animate: false, pickEdge: true);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (IsLoaded) SnapToEdge(animate: false);
    }

    private enum Edge { Left, Right, Top, Bottom }

    private const int VisibleClips = 4;
    private const double TileMargin = 4; // 2 on each side
    private static readonly Size PortraitTile = new(112, 124), LandscapeTile = new(132, 70);
    private Edge _edge = Edge.Right;

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(PortraitTile.Width, (d, _) => ((MainWindow)d).OnTileSizeChanged()));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(MainWindow),
        new PropertyMetadata(PortraitTile.Height, (d, _) => ((MainWindow)d).OnTileSizeChanged()));

    public static readonly DependencyProperty TextWidthProperty = DependencyProperty.Register(
        nameof(TextWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(PortraitTile.Width - 16));

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public double TextWidth { get => (double)GetValue(TextWidthProperty); set => SetValue(TextWidthProperty, value); }

    private bool Vertical => _edge is Edge.Left or Edge.Right;

    private void OnTileSizeChanged()
    {
        TextWidth = TileWidth - 16;
        Scroller.MaxHeight = Vertical ? VisibleClips * (TileHeight + TileMargin) : double.PositiveInfinity;
        Scroller.MaxWidth = Vertical ? double.PositiveInfinity : VisibleClips * (TileWidth + TileMargin);
    }

    /// <summary>Side edges get a column of portrait tiles, top/bottom edges a row of landscape ones.</summary>
    private void ApplyOrientation(bool animate = false)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Vertical ? Orientation.Vertical : Orientation.Horizontal);
        ClipList.ItemsPanel = new ItemsPanelTemplate(panel);
        Scroller.VerticalScrollBarVisibility = Vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Scroller.HorizontalScrollBarVisibility = Vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        SourceTitle.Visibility = Vertical ? Visibility.Collapsed : Visibility.Visible;

        var size = Vertical ? PortraitTile : LandscapeTile;
        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(GlideMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // FillBehavior.Stop with the base value set first, so the size stays put once the animation ends.
            void Morph(DependencyProperty property, double to)
            {
                var from = (double)GetValue(property);
                SetValue(property, to);
                BeginAnimation(property, new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            }
            Morph(TileWidthProperty, size.Width);
            Morph(TileHeightProperty, size.Height);
        }
        else
        {
            BeginAnimation(TileWidthProperty, null);
            BeginAnimation(TileHeightProperty, null);
            TileWidth = size.Width;
            TileHeight = size.Height;
        }
        OnTileSizeChanged();
    }

    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        var bar = Scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled ? 0 : 1;
        if (bar == 1) Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta / 2.0);
        else Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private const double GlideMs = 260;
    private readonly Stopwatch _glide = new();
    private Point _glideFrom;

    public void SnapToEdge(bool animate, bool pickEdge = false)
    {
        if (WorkArea() is not { } work) return;

        if (pickEdge)
        {
            double dl = Left - work.Left, dr = work.Right - (Left + ActualWidth);
            double dt = Top - work.Top, db = work.Bottom - (Top + ActualHeight);
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            var edge = min == dl ? Edge.Left : min == dr ? Edge.Right : min == dt ? Edge.Top : Edge.Bottom;
            bool reshape = (edge is Edge.Left or Edge.Right) != Vertical;
            _edge = edge;
            if (reshape) ApplyOrientation(animate);
            if (!animate) UpdateLayout(); // so ActualWidth/Height reflect the new shape
        }

        if (animate)
        {
            // The tiles morph at the same time, so the target is recomputed every frame from the live size.
            _glideFrom = new Point(Left, Top);
            _glide.Restart();
            CompositionTarget.Rendering -= OnGlideFrame;
            CompositionTarget.Rendering += OnGlideFrame;
            return;
        }
        if (_glide.IsRunning) return; // the running glide already tracks size changes

        var spot = Anchor(work);
        Left = spot.X;
        Top = spot.Y;
    }

    private void OnGlideFrame(object? sender, EventArgs e)
    {
        if (WorkArea() is not { } work) return;
        double t = Math.Min(1, _glide.Elapsed.TotalMilliseconds / GlideMs);
        double k = 1 - Math.Pow(1 - t, 3); // cubic ease-out
        var to = Anchor(work);
        Left = _glideFrom.X + (to.X - _glideFrom.X) * k;
        Top = _glideFrom.Y + (to.Y - _glideFrom.Y) * k;
        if (t < 1) return;
        _glide.Reset();
        CompositionTarget.Rendering -= OnGlideFrame;
    }

    /// <summary>Where the window rests on its edge, keeping its position along that edge.</summary>
    private Point Anchor(Rect work)
    {
        double minX = work.Left + SnapMargin, maxX = Math.Max(minX, work.Right - ActualWidth - SnapMargin);
        double minY = work.Top + SnapMargin, maxY = Math.Max(minY, work.Bottom - ActualHeight - SnapMargin);
        double x = _edge switch { Edge.Left => minX, Edge.Right => maxX, _ => Math.Clamp(Left, minX, maxX) };
        double y = _edge switch { Edge.Top => minY, Edge.Bottom => maxY, _ => Math.Clamp(Top, minY, maxY) };
        return new Point(x, y);
    }

    private Rect? WorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return null;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return null;

        var toDip = target.TransformFromDevice;
        return new Rect(toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top)),
                        toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

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
