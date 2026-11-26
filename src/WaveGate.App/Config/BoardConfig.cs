using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WaveGate.Overlay.Config;

public sealed class BoardConfig
{
    public List<ClipEntry> Clips { get; set; } = [];

    /// <summary>User-defined categories in display order. Clips refer to them by name.</summary>
    public List<string> Categories { get; set; } = [];

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Capture endpoint the APO is attached to, or null if setup has not been run.</summary>
    public string? MicDeviceId { get; set; }

    public float MicVolume { get; set; } = 1f;
    public float SidetoneVolume { get; set; } = 1f;

    /// <summary>What the overlay shows: null = Favorites, "" = all clips, otherwise a category name.</summary>
    public string? OverlaySource { get; set; }

    /// <summary>App whose audio the share button sends into the mic, by display name.</summary>
    public string? ShareSource { get; set; }

    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class HotkeySettings
{
    public string? ToggleOverlay { get; set; } = "Ctrl+Alt+W";
    public string? StopAll { get; set; } = "Ctrl+Alt+X";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetriggerMode
{
    /// <summary>Pressing a playing clip starts it again from the top.</summary>
    Restart,
    /// <summary>Each press adds another copy on top of the ones already playing.</summary>
    Overlap,
    /// <summary>Pressing a playing clip stops it.</summary>
    Stop,
}

public sealed class ClipEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string? _hotkey;
    private string? _category;
    private bool _favorite;
    private float _volume = 1f;
    private RetriggerMode _retrigger = RetriggerMode.Restart;
    private double _trimStart;
    private double? _trimEnd;
    private double _progress;
    private bool _paused;
    private bool _playing;
    private float[]? _peaks;
    private TimeSpan? _duration;
    private string _status = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>File name inside the clips folder.</summary>
    public string FileName { get; set; } = "";

    public DateTime Added { get; set; } = DateTime.Now;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string? Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }
    public string? Category { get => _category; set => Set(ref _category, value); }
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }

    /// <summary>Linear gain, 0..2.</summary>
    public float Volume { get => _volume; set => Set(ref _volume, value); }

    public RetriggerMode Retrigger { get => _retrigger; set => Set(ref _retrigger, value); }

    /// <summary>Seconds into the file where playback starts.</summary>
    public double TrimStart { get => _trimStart; set => Set(ref _trimStart, value); }

    /// <summary>Seconds into the file where playback stops; null = end of file.</summary>
    public double? TrimEnd { get => _trimEnd; set => Set(ref _trimEnd, value); }

    /// <summary>Playback position 0..1 for the tile's progress bar; not saved.</summary>
    [JsonIgnore]
    public double Progress { get => _progress; set { Set(ref _progress, value); UpdateStatus(); } }

    /// <summary>Playing but paused, for the tile's paused look; not saved.</summary>
    [JsonIgnore]
    public bool Paused { get => _paused; set { Set(ref _paused, value); UpdateStatus(); } }

    /// <summary>Has a voice in the engine (paused or not); not saved.</summary>
    [JsonIgnore]
    public bool Playing { get => _playing; set { Set(ref _playing, value); UpdateStatus(); } }

    /// <summary>Loudness shape of the trimmed clip for the tile; computed in the background, not saved.</summary>
    [JsonIgnore]
    public float[]? Peaks { get => _peaks; set => Set(ref _peaks, value); }

    /// <summary>Length of the trimmed clip, once analysed; not saved.</summary>
    [JsonIgnore]
    public TimeSpan? Duration { get => _duration; set { Set(ref _duration, value); UpdateStatus(); } }

    /// <summary>The tile's time line: length when idle, elapsed/length while playing.</summary>
    [JsonIgnore]
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Properties that only reflect playback or analysis, so changing them is not a config change.</summary>
    public static bool IsTransient(string? property) => property is nameof(Progress) or nameof(Paused) or nameof(Playing)
        or nameof(Peaks) or nameof(Duration) or nameof(Status);

    private void UpdateStatus()
    {
        static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        var elapsed = TimeSpan.FromSeconds((_duration?.TotalSeconds ?? 0) * _progress);
        Status = _duration is not { } length ? ""
            : _paused ? $"Paused · {Time(elapsed)}"
            : _playing ? $"{Time(elapsed)} / {Time(length)}"
            : Time(length);
    }

    /// <summary>True while the user drags the seek bar, so playback updates don't fight the drag.</summary>
    [JsonIgnore]
    public bool Scrubbing { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
