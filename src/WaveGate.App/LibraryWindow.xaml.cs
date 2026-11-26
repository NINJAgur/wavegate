using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WaveGate.Overlay.Config;

namespace WaveGate.Overlay;

/// <summary>The full soundboard: categories, search, clip management, volumes.</summary>
public partial class LibraryWindow : Window
{
    private const string ClipDragFormat = "WaveGate.Clip";

    private readonly IBoard _board;
    private readonly ListCollectionView _view;
    private readonly ObservableCollection<SidebarNode> _nodes = [];
    private SidebarNode _selected = SidebarNode.All;
    private bool _rebuildingSidebar;
    private Point _dragStart;
    private ClipEntry? _dragClip;

    /// <summary>Set by App before exiting; otherwise closing only hides the window.</summary>
    public bool AllowClose { get; set; }

    public LibraryWindow(IBoard board)
    {
        InitializeComponent();
        _board = board;

        _view = new ListCollectionView(board.Clips) { Filter = Matches };
        ApplySort();
        ClipGrid.ItemsSource = _view;
        CategoryList.ItemsSource = _nodes;

        foreach (var clip in board.Clips) clip.PropertyChanged += OnClipChanged;
        board.Clips.CollectionChanged += OnClipsChanged;
        board.Categories.CollectionChanged += (_, _) => RebuildSidebar();

        MicVolumeSlider.Value = board.MicVolume;
        SidetoneSlider.Value = board.SidetoneVolume;
        MicVolumeText.Text = $"{board.MicVolume:P0}";
        SidetoneText.Text = $"{board.SidetoneVolume:P0}";

        RebuildSidebar();
        RefreshView();
    }

    public void SetMicStatus(MicStatus status)
    {
        var (brush, text) = MicStatusText.Describe(status);
        MicDot.Fill = brush;
        MicText.Text = text;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---- Filtering ---------------------------------------------------------

    private bool Matches(object item)
    {
        if (item is not ClipEntry clip) return false;

        bool inNode = _selected.Kind switch
        {
            NodeKind.Favorites => clip.Favorite,
            NodeKind.Category => string.Equals(clip.Category, _selected.Category, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        if (!inNode) return false;

        var query = SearchBox.Text.Trim();
        return query.Length == 0 || clip.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshView()
    {
        _view.Refresh();
        ViewTitle.Text = _selected.Label;

        EmptyHint.Text = _board.Clips.Count == 0
            ? "Drop audio or video files here, or use Add sounds.\nMP3, WAV, OGG, MP4 and M4A work; for video only the sound is used."
            : SearchBox.Text.Trim().Length > 0 ? "No sounds match your search."
            : _selected.Kind == NodeKind.Favorites ? "No favorites yet. Click the ☆ on a sound to add it here and to the overlay."
            : "This category is empty. Drag sounds onto it in the sidebar, or drop files here while it is selected.";
        EmptyHint.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplySort()
    {
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(SortBox.SelectedIndex == 1
            ? new SortDescription(nameof(ClipEntry.Added), ListSortDirection.Descending)
            : new SortDescription(nameof(ClipEntry.Name), ListSortDirection.Ascending));
    }

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (ClipEntry clip in e.NewItems ?? Array.Empty<ClipEntry>()) clip.PropertyChanged += OnClipChanged;
        foreach (ClipEntry clip in e.OldItems ?? Array.Empty<ClipEntry>()) clip.PropertyChanged -= OnClipChanged;
        RebuildSidebar();
        RefreshView();
    }

    private void OnClipChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Progress ticks 30 times a second; only membership-affecting changes need a refresh.
        if (e.PropertyName is nameof(ClipEntry.Category) or nameof(ClipEntry.Favorite) or nameof(ClipEntry.Name))
        {
            RebuildSidebar();
            RefreshView();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshView();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view is null) return; // fires during InitializeComponent
        ApplySort();
    }

    // ---- Sidebar -----------------------------------------------------------

    private void RebuildSidebar()
    {
        _rebuildingSidebar = true;
        try
        {
            _nodes.Clear();
            _nodes.Add(SidebarNode.All with { Count = _board.Clips.Count });
            _nodes.Add(SidebarNode.Favorites with { Count = _board.Clips.Count(c => c.Favorite) });
            foreach (var name in _board.Categories)
            {
                int count = _board.Clips.Count(c => string.Equals(c.Category, name, StringComparison.OrdinalIgnoreCase));
                _nodes.Add(new SidebarNode(NodeKind.Category, name, name, count));
            }

            var keep = _nodes.FirstOrDefault(n => n.SameAs(_selected)) ?? _nodes[0];
            _selected = keep;
            CategoryList.SelectedItem = keep;
        }
        finally
        {
            _rebuildingSidebar = false;
        }
    }

    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingSidebar || CategoryList.SelectedItem is not SidebarNode node) return;
        _selected = node;
        RefreshView();
    }

    private void OnNewCategoryClick(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Ask(this, "New category", "Name", "");
        if (name is null) return;
        if (_board.AddCategory(name) is { } error)
        {
            MessageBox.Show(this, error, "WaveGate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CategoryList.SelectedItem = _nodes.FirstOrDefault(n => n.Kind == NodeKind.Category && n.Category == name.Trim());
    }

    private void OnCategoryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = CategoryList.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(MenuItem("New category…", () => OnNewCategoryClick(this, new RoutedEventArgs())));

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is SidebarNode { Kind: NodeKind.Category } node)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem($"Rename \"{node.Label}\"…", () => RenameCategory(node.Category!)));
            menu.Items.Add(MenuItem($"Delete \"{node.Label}\"", () => DeleteCategory(node.Category!)));
        }
    }

    private void RenameCategory(string name)
    {
        var newName = TextPromptWindow.Ask(this, "Rename category", "Name", name);
        if (newName is null) return;
        if (_board.RenameCategory(name, newName) is { } error)
            MessageBox.Show(this, error, "WaveGate", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteCategory(string name)
    {
        int count = _board.Clips.Count(c => string.Equals(c.Category, name, StringComparison.OrdinalIgnoreCase));
        var answer = MessageBox.Show(this,
            $"Delete the category \"{name}\"?\n\nIts {count} sound(s) stay in All sounds; only the category goes.",
            "WaveGate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _board.DeleteCategory(name);
    }

    private void OnCategoryDragOver(object sender, DragEventArgs e)
    {
        var node = (sender as ListBoxItem)?.DataContext as SidebarNode;
        e.Effects = node is not null && (e.Data.GetDataPresent(ClipDragFormat) || HasImportableFiles(e))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCategoryDrop(object sender, DragEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not SidebarNode node) return;
        e.Handled = true;

        if (e.Data.GetData(ClipDragFormat) is string id && _board.Clips.FirstOrDefault(c => c.Id.ToString() == id) is { } clip)
        {
            switch (node.Kind)
            {
                case NodeKind.Favorites: clip.Favorite = true; break;
                case NodeKind.Category: clip.Category = node.Category; break;
                case NodeKind.All: clip.Category = null; break;
            }
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _board.Import(files, node.Category, favorite: node.Kind == NodeKind.Favorites);
        }
    }

    // ---- Clips -------------------------------------------------------------

    private static ClipEntry? ClipOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipEntry;

    private void OnClipClick(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is { } clip) _board.Play(clip);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't let the click reach the tile and play the clip
        if (ClipOf(sender) is { } clip) clip.Favorite = !clip.Favorite;
    }

    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ClipOf(sender) is not { } clip || (sender as Button)?.ContextMenu is not { } menu) return;

        menu.Items.Clear();
        menu.Items.Add(MenuItem("Play", () => _board.Play(clip)));
        menu.Items.Add(MenuItem("Edit…", () => _board.Edit(clip)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Set hotkey…", () => _board.SetHotkey(clip)));
        var clear = MenuItem("Clear hotkey", () => _board.ClearHotkey(clip));
        clear.IsEnabled = clip.Hotkey is not null;
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        var fav = MenuItem("Favorite", () => clip.Favorite = !clip.Favorite);
        fav.IsChecked = clip.Favorite;
        menu.Items.Add(fav);

        var move = new MenuItem { Header = "Category" };
        var none = MenuItem("(none)", () => clip.Category = null);
        none.IsChecked = clip.Category is null;
        move.Items.Add(none);
        foreach (var name in _board.Categories)
        {
            var item = MenuItem(name, () => clip.Category = name);
            item.IsChecked = string.Equals(clip.Category, name, StringComparison.OrdinalIgnoreCase);
            move.Items.Add(item);
        }
        menu.Items.Add(move);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Remove from board", () => _board.Remove(clip)));
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragClip = ClipOf(sender);
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragClip is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var clip = _dragClip;
        _dragClip = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ClipDragFormat, clip.Id.ToString()), DragDropEffects.Copy);
    }

    // ---- Import ------------------------------------------------------------

    private string? ImportCategory => _selected.Kind == NodeKind.Category ? _selected.Category : null;

    private static bool HasImportableFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        files.Any(f => Directory.Exists(f) || ConfigStore.IsSupported(f));

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImportableFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _board.Import(files, ImportCategory, favorite: _selected.Kind == NodeKind.Favorites);
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Add sounds",
            Filter = "Audio and video|" + string.Join(";", ConfigStore.SupportedExtensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(this) == true)
            _board.Import(dialog.FileNames, ImportCategory, favorite: _selected.Kind == NodeKind.Favorites);
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = true, Title = "Add a folder of sounds" };
        if (dialog.ShowDialog(this) == true)
            _board.Import(dialog.FolderNames, ImportCategory, favorite: false);
    }

    private void OnStopAllClick(object sender, RoutedEventArgs e) => _board.StopAll();

    // ---- Volumes -----------------------------------------------------------

    private void OnMicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MicVolumeText.Text = $"{e.NewValue:P0}";
        if (_board is not null) _board.MicVolume = (float)e.NewValue;
    }

    private void OnSidetoneChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SidetoneText.Text = $"{e.NewValue:P0}";
        if (_board is not null) _board.SidetoneVolume = (float)e.NewValue;
    }

    // ---- Helpers -----------------------------------------------------------

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private enum NodeKind { All, Favorites, Category }

    private sealed record SidebarNode(NodeKind Kind, string Label, string? Category, int Count)
    {
        public static readonly SidebarNode All = new(NodeKind.All, "All sounds", null, 0);
        public static readonly SidebarNode Favorites = new(NodeKind.Favorites, "★ Favorites", null, 0);

        public bool SameAs(SidebarNode other) =>
            Kind == other.Kind && string.Equals(Category, other.Category, StringComparison.OrdinalIgnoreCase);
    }
}
