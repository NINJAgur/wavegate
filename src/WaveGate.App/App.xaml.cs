using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WaveGate.Audio;
using WaveGate.Overlay.Config;
using WaveGate.Overlay.Hotkeys;
using WaveGate.Overlay.Setup;
using WaveGate.Overlay.Tray;

namespace WaveGate.Overlay;

/// <summary>Owns config, audio engine, hotkeys, tray, overlay and library, and wires them together.</summary>
public partial class App : Application, IBoard
{
    private readonly ConfigStore _store = new();
    private BoardConfig _config = new();
    private Mutex? _singleInstance;
    private SoundEngine? _engine;
    private MainWindow? _window;
    private LibraryWindow? _library;
    private GlobalHotkeys? _hotkeys;
    private TrayIcon? _tray;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _progressTimer;
    private DispatcherTimer? _saveTimer;
    private HashSet<Guid> _playing = [];
    private bool _setupRunning;

    public ObservableCollection<ClipEntry> Clips { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second instance could not register the same hotkeys anyway.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\WaveGate.Overlay", out bool isFirst);
        if (!isFirst)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _config = _store.Load();
        _config.MicDeviceId ??= MicSetup.FindAttachedMic();
        foreach (var name in _config.Categories) Categories.Add(name);
        foreach (var clip in _config.Clips)
        {
            Clips.Add(clip);
            clip.PropertyChanged += OnClipChanged;
            if (clip.Category is { } c && !Categories.Contains(c, StringComparer.OrdinalIgnoreCase)) Categories.Add(c);
        }
        // Boards from before favorites existed would otherwise open to an empty overlay.
        if (_config.OverlaySource is null && Clips.Count > 0 && !Clips.Any(c => c.Favorite)) _config.OverlaySource = "";
        Clips.CollectionChanged += (_, _) => ScheduleSave();
        Categories.CollectionChanged += (_, _) => ScheduleSave();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => Save();

        _tray = new TrayIcon(() => _config.MicDeviceId);
        _tray.OpenLibraryRequested += ShowLibrary;
        _tray.ToggleOverlayRequested += ToggleOverlay;
        _tray.StopAllRequested += StopAll;
        _tray.AttachRequested += AttachMic;
        _tray.DetachRequested += DetachMic;
        _tray.ExitRequested += ExitApp;

        _engine = TryCreateEngine(out var engineError);
        if (_engine is null) _tray.Notify("Headphone output unavailable", engineError!, isError: true);

        _window = new MainWindow(this);
        RestorePosition(_window);
        _window.ExitClicked += ExitApp;

        _library = new LibraryWindow(this);
        _library.HiddenByUser += ShowOverlay;

        _hotkeys = new GlobalHotkeys(_window);
        RegisterHotkeys();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateMicStatus();
        _statusTimer.Start();
        UpdateMicStatus();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        // One of the two is visible at a time: the library while managing, the overlay while playing.
        _library.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_window is not null)
        {
            SavePosition();
            Save();
        }
        _statusTimer?.Stop();
        _progressTimer?.Stop();
        _saveTimer?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _engine?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ExitApp()
    {
        if (_library is not null) _library.AllowClose = true;
        Shutdown();
    }

    // ---- Playback ----------------------------------------------------------

    /// <summary>Hotkeys and the "Play" menu item: a paused clip resumes, a playing one follows its retrigger setting.</summary>
    public void Play(ClipEntry clip)
    {
        if (!EnsureEngine(out var engine)) return;

        if (engine.IsPaused(clip.Id))
        {
            engine.SetPaused(clip.Id, false);
            return;
        }

        if (engine.IsPlaying(clip.Id))
        {
            switch (clip.Retrigger)
            {
                case RetriggerMode.Stop:
                    engine.Stop(clip.Id);
                    return;
                case RetriggerMode.Restart:
                    engine.Stop(clip.Id);
                    break;
            }
        }

        StartVoice(clip, new PlayOptions(clip.Volume, clip.TrimStart, clip.TrimEnd));
    }

    private ClipEntry? _lastPress;
    private long _lastPressAt;

    /// <summary>Clicking a tile: play, or pause/resume if it's already going. A double-click restarts it.</summary>
    public void Press(ClipEntry clip)
    {
        if (!EnsureEngine(out var engine)) return;

        long now = Environment.TickCount64;
        bool isDouble = _lastPress == clip && now - _lastPressAt <= System.Windows.Forms.SystemInformation.DoubleClickTime;
        _lastPress = isDouble ? null : clip; // a third click starts a fresh sequence
        _lastPressAt = now;

        if (isDouble)
        {
            // The first click of the pair already paused (or started) it; the second restarts from the top.
            engine.Stop(clip.Id);
            StartVoice(clip, new PlayOptions(clip.Volume, clip.TrimStart, clip.TrimEnd));
        }
        else if (engine.IsPlaying(clip.Id))
        {
            engine.SetPaused(clip.Id, !engine.IsPaused(clip.Id));
        }
        else
        {
            StartVoice(clip, new PlayOptions(clip.Volume, clip.TrimStart, clip.TrimEnd));
        }
    }

    private bool EnsureEngine(out SoundEngine engine)
    {
        engine = _engine!;
        if (_engine is not null) return true;
        Notify("Headphone output unavailable", "Restart WaveGate after connecting an output device.", isError: true);
        return false;
    }

    public void Seek(ClipEntry clip, double fraction)
    {
        if (_engine is null) return;
        try
        {
            if (_engine.Seek(clip.Id, fraction)) return;
        }
        catch (Exception ex) // decoders throw a wide range of types for bad files
        {
            Notify($"Couldn't seek \"{clip.Name}\"", ex.Message, isError: true);
            return;
        }
        StartVoice(clip, new PlayOptions(clip.Volume, clip.TrimStart, clip.TrimEnd), fraction);
    }

    private void StartVoice(ClipEntry clip, PlayOptions options, double startAt = 0)
    {
        if (_engine is null) return;

        var path = _store.ClipPath(clip);
        if (!File.Exists(path))
        {
            Notify($"Missing file for \"{clip.Name}\"", path, isError: true);
            return;
        }

        try
        {
            _engine.Play(clip.Id, path, options, startAt);
        }
        catch (Exception ex) // decoders throw a wide range of types for bad files
        {
            Notify($"Can't play \"{clip.Name}\"", ex.Message, isError: true);
        }
    }

    public IReadOnlyList<AudioApp> AudioApps()
    {
        try
        {
            return AppAudioCapture.ListApps();
        }
        catch (Exception ex) // COM errors while the audio service restarts
        {
            Notify("Couldn't list apps playing audio", ex.Message, isError: true);
            return [];
        }
    }

    public event Action? SharingChanged;

    public string? ShareSource
    {
        get => _config.ShareSource;
        set
        {
            _config.ShareSource = value;
            ScheduleSave();
            Sharing = value is not null; // picking a source starts sharing it
        }
    }

    private bool _sharing;

    public bool Sharing
    {
        get => _sharing;
        set
        {
            _sharing = value && StartSharing();
            if (!_sharing) _engine?.ShareAppAudio(null);
            SharingChanged?.Invoke();
        }
    }

    private bool StartSharing()
    {
        if (_engine is null) return false;
        if (ShareSource is not { } name)
        {
            Notify("Choose what to share", "Use the arrow next to the share button to pick an app.");
            return false;
        }

        // Processes come and go, so the source is remembered by name and resolved each time.
        if (AudioApps().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } app)
        {
            Notify($"{name} isn't playing audio", "Start something in it, then press the share button again.");
            return false;
        }

        try
        {
            _engine.ShareAppAudio(app.ProcessId);
            return true;
        }
        catch (Exception ex) // needs Windows 10 2004+; the app may also have just closed
        {
            Notify($"Couldn't share audio from {name}", ex.Message, isError: true);
            return false;
        }
    }

    public void StopAll() => _engine?.StopAll();

    public float MicVolume
    {
        get => _config.MicVolume;
        set
        {
            _config.MicVolume = value;
            if (_engine is not null) _engine.MicVolume = value;
            ScheduleSave();
        }
    }

    public float SidetoneVolume
    {
        get => _config.SidetoneVolume;
        set
        {
            _config.SidetoneVolume = value;
            if (_engine is not null) _engine.SidetoneVolume = value;
            ScheduleSave();
        }
    }

    private void UpdateProgress()
    {
        var progress = _engine?.GetProgress() ?? [];
        if (progress.Count == 0 && _playing.Count == 0) return;

        foreach (var clip in Clips)
        {
            if (clip.Scrubbing) continue;
            if (progress.TryGetValue(clip.Id, out var p)) clip.Progress = p;
            else if (_playing.Contains(clip.Id)) clip.Progress = 0;
        }
        _playing = [.. progress.Keys];
    }

    private SoundEngine? TryCreateEngine(out string? error)
    {
        try
        {
            error = null;
            return new SoundEngine { MicVolume = _config.MicVolume, SidetoneVolume = _config.SidetoneVolume };
        }
        catch (Exception ex) // no render device, device busy, driver error...
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Setup restarts Audiosrv, which kills our WASAPI stream; rebuild it once the service is back.</summary>
    private async Task RestartEngineAsync()
    {
        _engine?.Dispose();
        _engine = null;

        string? error = null;
        for (int attempt = 0; attempt < 10 && _engine is null; attempt++)
        {
            await Task.Delay(500);
            _engine = TryCreateEngine(out error);
        }
        if (_engine is null) Notify("Headphone output unavailable", error ?? "", isError: true);
        else if (_sharing) Sharing = true;
    }

    // ---- Board -------------------------------------------------------------

    public string? OverlaySource
    {
        get => _config.OverlaySource;
        set
        {
            _config.OverlaySource = value;
            ScheduleSave();
        }
    }

    public void Import(IEnumerable<string> paths, string? category, bool favorite)
    {
        int added = 0;
        foreach (var (file, fileCategory) in ConfigStore.ExpandImports(paths, category))
        {
            try
            {
                var clip = _store.Import(file);
                clip.Category = fileCategory;
                clip.Favorite = favorite;
                if (fileCategory is not null && !Categories.Contains(fileCategory, StringComparer.OrdinalIgnoreCase))
                    Categories.Add(fileCategory);
                clip.PropertyChanged += OnClipChanged;
                Clips.Add(clip);
                added++;
            }
            catch (IOException ex)
            {
                Notify($"Could not add {Path.GetFileName(file)}", ex.Message, isError: true);
            }
        }
        if (added == 0) Notify("Nothing added", "No MP3, WAV, OGG, MP4 or M4A files were found.");
    }

    public void Remove(ClipEntry clip)
    {
        // The copied audio file stays in the clips folder; only the board entry goes.
        _engine?.Stop(clip.Id);
        clip.PropertyChanged -= OnClipChanged;
        Clips.Remove(clip);
        RegisterHotkeys();
    }

    public void Edit(ClipEntry clip)
    {
        TimeSpan? duration = null;
        try
        {
            duration = SoundEngine.GetDuration(_store.ClipPath(clip));
        }
        catch (Exception) // unreadable file: the editor just hides trimming
        {
        }

        var editor = new ClipEditorWindow(clip, Categories, duration,
            preview: options => { _engine?.Stop(clip.Id); StartVoice(clip, options); },
            stopPreview: () => _engine?.Stop(clip.Id),
            setHotkey: () => SetHotkey(clip),
            clearHotkey: () => ClearHotkey(clip));
        if (_library is { IsVisible: true }) editor.Owner = _library;

        if (editor.ShowDialog() == true && clip.Category is { } c && !Categories.Contains(c, StringComparer.OrdinalIgnoreCase))
            Categories.Add(c);
    }

    public string? AddCategory(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Enter a name.";
        if (Categories.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"\"{name}\" already exists.";
        Categories.Add(name);
        _window?.Refresh();
        return null;
    }

    public string? RenameCategory(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return "Enter a name.";
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) &&
            Categories.Contains(newName, StringComparer.OrdinalIgnoreCase))
            return $"\"{newName}\" already exists.";

        int index = Categories.IndexOf(oldName);
        if (index < 0) return null;
        Categories[index] = newName;
        foreach (var clip in Clips.Where(c => string.Equals(c.Category, oldName, StringComparison.OrdinalIgnoreCase)))
            clip.Category = newName;
        if (string.Equals(_config.OverlaySource, oldName, StringComparison.OrdinalIgnoreCase)) OverlaySource = newName;
        _window?.Refresh();
        return null;
    }

    public void DeleteCategory(string name)
    {
        // Clips stay on the board; they just lose the category.
        foreach (var clip in Clips.Where(c => string.Equals(c.Category, name, StringComparison.OrdinalIgnoreCase)))
            clip.Category = null;
        Categories.Remove(name);
        _window?.Refresh();
    }

    private void OnClipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClipEntry.Progress)) ScheduleSave();
    }

    // ---- Hotkeys -----------------------------------------------------------

    private void RegisterHotkeys()
    {
        if (_hotkeys is not { } hotkeys) return;
        hotkeys.UnregisterAll();

        var failed = new List<string>();
        void Bind(string? text, string label, Action action)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!Hotkey.TryParse(text, out var hotkey) || !hotkeys.Register(hotkey, action))
                failed.Add($"{label} ({text})");
        }

        Bind(_config.Hotkeys.ToggleOverlay, "Show/hide overlay", ToggleOverlay);
        Bind(_config.Hotkeys.StopAll, "Stop all", StopAll);
        foreach (var clip in Clips)
            Bind(clip.Hotkey, clip.Name, () => Play(clip));

        if (failed.Count > 0)
            Notify("Some hotkeys are unavailable", "Invalid or used by another app: " + string.Join(", ", failed), isError: true);
    }

    public void SetHotkey(ClipEntry clip)
    {
        // Release everything so the combination reaches the dialog instead of firing.
        _hotkeys?.UnregisterAll();
        try
        {
            var dialog = new HotkeyCaptureWindow(clip.Name);
            if (dialog.ShowDialog() != true || dialog.Result is not { } hotkey) return;

            if (FindHotkeyOwner(hotkey, except: clip) is { } owner)
            {
                MessageBox.Show($"{hotkey} is already used by \"{owner}\".", "WaveGate",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            clip.Hotkey = hotkey.ToString();
        }
        finally
        {
            RegisterHotkeys();
        }
    }

    public void ClearHotkey(ClipEntry clip)
    {
        clip.Hotkey = null;
        RegisterHotkeys();
    }

    private string? FindHotkeyOwner(Hotkey hotkey, ClipEntry except)
    {
        bool Same(string? text) => Hotkey.TryParse(text, out var other) && other == hotkey;

        if (Same(_config.Hotkeys.ToggleOverlay)) return "Show/hide overlay";
        if (Same(_config.Hotkeys.StopAll)) return "Stop all";
        return Clips.FirstOrDefault(c => c != except && Same(c.Hotkey))?.Name;
    }

    // ---- Microphone setup (elevated) ---------------------------------------

    public string? MicDeviceId => _config.MicDeviceId;

    public async void AttachMic(string deviceId)
    {
        if (_setupRunning) return;
        _setupRunning = true;
        try
        {
            // Already on another mic: move it over instead of attaching a second one.
            var current = _config.MicDeviceId;
            Task<SetupResult> Run(bool replace) => current is not null && current != deviceId
                ? MicSetup.SwitchAsync(current, deviceId, replace)
                : MicSetup.InstallAsync(deviceId, replace);

            var result = await Run(replace: false);
            if (result.Outcome == SetupOutcome.SlotOccupied)
            {
                var answer = MessageBox.Show(
                    "This microphone's driver already uses the effect slot WaveGate needs " +
                    "(often vendor noise suppression or EQ).\n\n" +
                    "Replace it while WaveGate is attached? Detaching restores it.",
                    "WaveGate", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                result = await Run(replace: true);
            }

            if (result.Outcome == SetupOutcome.Ok) _config.MicDeviceId = deviceId;
            await FinishSetupAsync(result, "WaveGate is attached to the microphone.");
        }
        finally
        {
            _setupRunning = false;
        }
    }

    public async void DetachMic()
    {
        if (_setupRunning || _config.MicDeviceId is not { } deviceId) return;
        _setupRunning = true;
        try
        {
            var result = await MicSetup.UninstallAsync(deviceId);
            if (result.Outcome == SetupOutcome.Ok) _config.MicDeviceId = null;
            await FinishSetupAsync(result, "WaveGate is detached and the original settings are restored.");
        }
        finally
        {
            _setupRunning = false;
        }
    }

    private async Task FinishSetupAsync(SetupResult result, string successMessage)
    {
        if (result.Outcome == SetupOutcome.Cancelled) return;

        Save();
        await RestartEngineAsync(); // the installer may have restarted Audiosrv even if it then failed

        if (result.Outcome == SetupOutcome.Ok)
            Notify("WaveGate", successMessage);
        else
            MessageBox.Show(result.Message ?? "Setup failed.", "WaveGate setup", MessageBoxButton.OK, MessageBoxImage.Error);

        UpdateMicStatus();
    }

    private void UpdateMicStatus()
    {
        var status = _engine?.IsMicLinked == true ? MicStatus.Live
            : _config.MicDeviceId is null ? MicStatus.NotSetUp
            : MicStatus.Waiting;
        _window?.SetMicStatus(status);
        _library?.SetMicStatus(status);
    }

    // ---- Windows / persistence ---------------------------------------------

    public void ShowLibrary()
    {
        if (_library is null) return;
        if (_window is { IsVisible: true })
        {
            SavePosition();
            _window.Hide();
        }
        _library.Show();
        if (_library.WindowState == WindowState.Minimized) _library.WindowState = WindowState.Normal;
        _library.Activate();
    }

    private void ShowOverlay() => _window?.Show(); // WS_EX_NOACTIVATE: shows without taking focus

    private void ToggleOverlay()
    {
        if (_window is null) return;
        if (_window.IsVisible)
        {
            SavePosition();
            _window.Hide();
        }
        else
        {
            _window.Show(); // WS_EX_NOACTIVATE: shows without taking focus
        }
    }

    private void RestorePosition(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (_config.Left is { } left && _config.Top is { } top && screen.Contains(new Point(left + 20, top + 20)))
        {
            window.Left = left;
            window.Top = top;
            return;
        }

        // Default: top-right of the primary work area, out of the way.
        var work = SystemParameters.WorkArea;
        window.Left = work.Right - 120; // the overlay sizes to content and snaps to the right edge on show
        window.Top = work.Top + 16;
    }

    private void SavePosition()
    {
        if (_window is null) return;
        _config.Left = _window.Left;
        _config.Top = _window.Top;
    }

    /// <summary>Coalesces bursts of edits (sliders, folder imports) into one write.</summary>
    private void ScheduleSave()
    {
        if (_saveTimer is null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        _saveTimer?.Stop();
        _config.Clips = [.. Clips];
        _config.Categories = [.. Categories];
        try
        {
            _store.Save(_config);
        }
        catch (IOException ex)
        {
            Notify("Could not save the board", ex.Message, isError: true);
        }
    }

    private void Notify(string title, string text, bool isError = false) => _tray?.Notify(title, text, isError);

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive; a soundboard that vanishes mid-game is worse than a missed click.
        Notify("Something went wrong", e.Exception.Message, isError: true);
        e.Handled = true;
    }
}
