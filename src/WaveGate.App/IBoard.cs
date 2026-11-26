using System.Collections.ObjectModel;
using System.Windows.Media;
using WaveGate.Audio;
using WaveGate.Overlay.Config;

namespace WaveGate.Overlay;

/// <summary>What the overlay and library windows may ask of the app. App implements it.</summary>
public interface IBoard
{
    ObservableCollection<ClipEntry> Clips { get; }
    ObservableCollection<string> Categories { get; }

    float MicVolume { get; set; }
    float SidetoneVolume { get; set; }

    /// <summary>What the overlay shows: null = Favorites, "" = all clips, otherwise a category name.</summary>
    string? OverlaySource { get; set; }

    void Play(ClipEntry clip);
    /// <summary>Jumps a playing clip to <paramref name="fraction"/> of its length, or starts it there.</summary>
    void Seek(ClipEntry clip, double fraction);
    /// <summary>Apps that currently have an audio session on the default output.</summary>
    IReadOnlyList<AudioApp> AudioApps();
    /// <summary>Streams one app's audio into the mic (others hear it; you already do). Null stops.</summary>
    AudioApp? SharedApp { get; set; }
    /// <summary>The microphone WaveGate is attached to, if any.</summary>
    string? MicDeviceId { get; }
    void AttachMic(string deviceId);
    void DetachMic();
    void StopAll();
    void Edit(ClipEntry clip);
    void SetHotkey(ClipEntry clip);
    void ClearHotkey(ClipEntry clip);
    void Remove(ClipEntry clip);
    void Import(IEnumerable<string> paths, string? category, bool favorite);
    void ShowLibrary();

    /// <summary>Returns an error message, or null on success.</summary>
    string? AddCategory(string name);

    /// <summary>Returns an error message, or null on success.</summary>
    string? RenameCategory(string oldName, string newName);

    void DeleteCategory(string name);
}

public enum MicStatus { NotSetUp, Waiting, Live }

public static class MicStatusText
{
    public static (Brush Brush, string Text) Describe(MicStatus status) => status switch
    {
        MicStatus.Live => (new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), "Live: clips go into the microphone"),
        MicStatus.Waiting => (new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), "Waiting for an app to use the microphone"),
        _ => (new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), "No microphone set up (tray icon > Microphone)"),
    };
}
