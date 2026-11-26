using System.ComponentModel;

namespace WaveGate.Overlay.Config;

public sealed class BoardConfig
{
    public List<ClipEntry> Clips { get; set; } = [];
    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Capture endpoint the APO is attached to, or null if setup has not been run.</summary>
    public string? MicDeviceId { get; set; }

    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class HotkeySettings
{
    public string? ToggleOverlay { get; set; } = "Ctrl+Alt+W";
    public string? StopAll { get; set; } = "Ctrl+Alt+X";
}

public sealed class ClipEntry : INotifyPropertyChanged
{
    private string? _hotkey;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>File name inside the clips folder.</summary>
    public string FileName { get; set; } = "";

    public string? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hotkey)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
