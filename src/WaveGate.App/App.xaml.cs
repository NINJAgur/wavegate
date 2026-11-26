using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WaveGate.Audio;
using WaveGate.Overlay.Config;
using WaveGate.Overlay.Hotkeys;
using WaveGate.Overlay.Setup;
using WaveGate.Overlay.Tray;

namespace WaveGate.Overlay;

/// <summary>Owns config, audio engine, hotkeys, tray and overlay, and wires them together.</summary>
public partial class App : Application
{
    private readonly ConfigStore _store = new();
    private readonly ObservableCollection<ClipEntry> _clips = [];
    private BoardConfig _config = new();
    private Mutex? _singleInstance;
    private SoundEngine? _engine;
    private MainWindow? _window;
    private GlobalHotkeys? _hotkeys;
    private TrayIcon? _tray;
    private DispatcherTimer? _statusTimer;
    private bool _setupRunning;

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
        foreach (var clip in _config.Clips) _clips.Add(clip);

        _tray = new TrayIcon(() => _config.MicDeviceId);
        _tray.ToggleOverlayRequested += ToggleOverlay;
        _tray.StopAllRequested += StopAll;
        _tray.AttachRequested += AttachMic;
        _tray.DetachRequested += DetachMic;
        _tray.ExitRequested += Shutdown;

        _engine = TryCreateEngine(out var engineError);
        if (_engine is null) _tray.Notify("Headphone output unavailable", engineError!, isError: true);

        _window = new MainWindow(_clips);
        RestorePosition(_window);
        _window.ClipClicked += PlayClip;
        _window.SetHotkeyRequested += SetHotkey;
        _window.ClearHotkeyRequested += ClearHotkey;
        _window.RemoveRequested += RemoveClip;
        _window.FilesDropped += ImportFiles;
        _window.StopAllClicked += StopAll;
        _window.HideClicked += ToggleOverlay;

        _hotkeys = new GlobalHotkeys(_window);
        RegisterHotkeys();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateMicStatus();
        _statusTimer.Start();
        UpdateMicStatus();

        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_window is not null)
        {
            SavePosition();
            Save();
        }
        _statusTimer?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _engine?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---- Playback ----------------------------------------------------------

    private void PlayClip(ClipEntry clip)
    {
        if (_engine is null)
        {
            Notify("Headphone output unavailable", "Restart WaveGate after connecting an output device.", isError: true);
            return;
        }

        var path = _store.ClipPath(clip);
        if (!File.Exists(path))
        {
            Notify($"Missing file for \"{clip.Name}\"", path, isError: true);
            return;
        }

        try
        {
            _engine.Play(path);
        }
        catch (Exception ex) // decoders throw a wide range of types for bad files
        {
            Notify($"Can't play \"{clip.Name}\"", ex.Message, isError: true);
        }
    }

    private void StopAll() => _engine?.StopAll();

    private static SoundEngine? TryCreateEngine(out string? error)
    {
        try
        {
            error = null;
            return new SoundEngine();
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
    }

    // ---- Board -------------------------------------------------------------

    private void ImportFiles(string[] files)
    {
        foreach (var file in files)
        {
            try
            {
                _clips.Add(_store.Import(file));
            }
            catch (IOException ex)
            {
                Notify($"Could not add {Path.GetFileName(file)}", ex.Message, isError: true);
            }
        }
        Save();
    }

    private void RemoveClip(ClipEntry clip)
    {
        // The copied audio file stays in the clips folder; only the board entry goes.
        _clips.Remove(clip);
        Save();
        RegisterHotkeys();
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
        foreach (var clip in _clips)
            Bind(clip.Hotkey, clip.Name, () => PlayClip(clip));

        if (failed.Count > 0)
            Notify("Some hotkeys are unavailable", "Invalid or used by another app: " + string.Join(", ", failed), isError: true);
    }

    private void SetHotkey(ClipEntry clip)
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
            Save();
        }
        finally
        {
            RegisterHotkeys();
        }
    }

    private void ClearHotkey(ClipEntry clip)
    {
        clip.Hotkey = null;
        Save();
        RegisterHotkeys();
    }

    private string? FindHotkeyOwner(Hotkey hotkey, ClipEntry except)
    {
        bool Same(string? text) => Hotkey.TryParse(text, out var other) && other == hotkey;

        if (Same(_config.Hotkeys.ToggleOverlay)) return "Show/hide overlay";
        if (Same(_config.Hotkeys.StopAll)) return "Stop all";
        return _clips.FirstOrDefault(c => c != except && Same(c.Hotkey))?.Name;
    }

    // ---- Microphone setup (elevated) ---------------------------------------

    private async void AttachMic(string deviceId)
    {
        if (_setupRunning) return;
        _setupRunning = true;
        try
        {
            var result = await MicSetup.InstallAsync(deviceId, replaceVendorEffect: false);
            if (result.Outcome == SetupOutcome.SlotOccupied)
            {
                var answer = MessageBox.Show(
                    "This microphone's driver already uses the effect slot WaveGate needs " +
                    "(often vendor noise suppression or EQ).\n\n" +
                    "Replace it while WaveGate is attached? Detaching restores it.",
                    "WaveGate", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                result = await MicSetup.InstallAsync(deviceId, replaceVendorEffect: true);
            }

            if (result.Outcome == SetupOutcome.Ok) _config.MicDeviceId = deviceId;
            await FinishSetupAsync(result, "WaveGate is attached to the microphone.");
        }
        finally
        {
            _setupRunning = false;
        }
    }

    private async void DetachMic()
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
    }

    // ---- Window / persistence ----------------------------------------------

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
        window.Left = work.Right - window.Width - 16;
        window.Top = work.Top + 16;
    }

    private void SavePosition()
    {
        if (_window is null) return;
        _config.Left = _window.Left;
        _config.Top = _window.Top;
    }

    private void Save()
    {
        _config.Clips = [.. _clips];
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
        // Keep the overlay alive; a soundboard that vanishes mid-game is worse than a missed click.
        Notify("Something went wrong", e.Exception.Message, isError: true);
        e.Handled = true;
    }
}
