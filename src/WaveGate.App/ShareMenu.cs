using System.Windows;
using System.Windows.Controls;

namespace WaveGate.Overlay;

/// <summary>The share button's dropdown, used by both the library and the overlay.</summary>
public static class ShareMenu
{
    public static void Open(IBoard board, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var off = new MenuItem { Header = "Don't share", IsChecked = !board.Sharing };
        off.Click += (_, _) => board.Sharing = false;
        menu.Items.Add(off);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Send audio from…", IsEnabled = false });

        var apps = board.AudioApps();
        if (apps.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No apps are playing audio", IsEnabled = false });
        if (board.ShareSource is { } saved && apps.All(a => !string.Equals(a.Name, saved, StringComparison.OrdinalIgnoreCase)))
            menu.Items.Add(new MenuItem { Header = $"{saved} (not playing)", IsEnabled = false });

        foreach (var app in apps)
        {
            var item = new MenuItem
            {
                Header = app.Name,
                IsChecked = board.Sharing && string.Equals(app.Name, board.ShareSource, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => board.ShareSource = app.Name;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
