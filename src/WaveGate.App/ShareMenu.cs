using System.Windows;
using System.Windows.Controls;

namespace WaveGate.Overlay;

/// <summary>The share button's dropdown, used by both the library and the overlay.</summary>
public static class ShareMenu
{
    public static void Open(IBoard board, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        menu.Items.Add(new MenuItem { Header = "Send audio from…", IsEnabled = false });

        var apps = board.AudioApps();
        if (apps.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No apps are playing audio", IsEnabled = false });
        if (board.ShareSource is { } saved && apps.All(a => !string.Equals(a.Name, saved, StringComparison.OrdinalIgnoreCase)))
            menu.Items.Add(new MenuItem { Header = $"{saved} (not playing)", IsChecked = true, IsEnabled = false });

        foreach (var app in apps)
        {
            var item = new MenuItem
            {
                Header = app.Name,
                IsChecked = string.Equals(app.Name, board.ShareSource, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => board.ShareSource = app.Name;
            menu.Items.Add(item);
        }

        if (board.Sharing)
        {
            menu.Items.Add(new Separator());
            var stop = new MenuItem { Header = "Stop sharing" };
            stop.Click += (_, _) => board.Sharing = false;
            menu.Items.Add(stop);
        }
        menu.IsOpen = true;
    }
}
