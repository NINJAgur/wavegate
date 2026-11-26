using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;

namespace WaveGate.Overlay.Setup;

public sealed record Microphone(string Id, string Name);

public enum SetupOutcome { Ok, Cancelled, SlotOccupied, Failed }

public sealed record SetupResult(SetupOutcome Outcome, string? Message = null);

/// <summary>
/// Runs WaveGate.Installer.exe elevated. This is the only point where WaveGate asks for UAC.
/// Expects WaveGate.Installer.exe and WaveGateApo.dll next to WaveGate.exe.
/// </summary>
public static class MicSetup
{
    private const int ExitSlotOccupied = 3;
    private const int ErrorCancelled = 1223; // user dismissed the UAC prompt

    private static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "WaveGate.Installer.exe");
    private static string ApoPath => Path.Combine(AppContext.BaseDirectory, "WaveGateApo.dll");
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveGate", "installer.log");

    /// <summary>
    /// Finds a mic attached outside the app (e.g. the installer run by hand): every live attachment
    /// has a backup file, and detach renames it to *.restored-*.json.
    /// </summary>
    public static string? FindAttachedMic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveGate", "backup");
        if (!Directory.Exists(dir)) return null;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Where(f => !f.Contains(".restored-")))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("DeviceId", out var id) && id.GetString() is { } deviceId) return deviceId;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // Unreadable backup: ignore it, the user can still attach from the tray.
            }
        }
        return null;
    }

    public static IReadOnlyList<Microphone> ListMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<Microphone>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new Microphone(device.ID, device.FriendlyName));
        }
        return result;
    }

    public static Task<SetupResult> InstallAsync(string deviceId, bool replaceVendorEffect)
    {
        var args = new List<string> { "install", ApoPath, deviceId };
        if (replaceVendorEffect) args.Add("--replace");
        return RunElevatedAsync(args);
    }

    /// <summary>Moves the APO from one mic to another in a single elevated run and audio restart.</summary>
    public static Task<SetupResult> SwitchAsync(string fromId, string toId, bool replaceVendorEffect)
    {
        var args = new List<string> { "switch", fromId, toId };
        if (replaceVendorEffect) args.Add("--replace");
        return RunElevatedAsync(args);
    }

    public static Task<SetupResult> UninstallAsync(string deviceId) =>
        RunElevatedAsync(["uninstall", deviceId]);

    private static async Task<SetupResult> RunElevatedAsync(IEnumerable<string> args)
    {
        if (!File.Exists(InstallerPath))
            return new SetupResult(SetupOutcome.Failed, $"Missing {InstallerPath}");

        var psi = new ProcessStartInfo(InstallerPath)
        {
            UseShellExecute = true, // required for Verb = runas
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new SetupResult(SetupOutcome.Cancelled);
        }

        if (process is null) return new SetupResult(SetupOutcome.Failed, "Could not start the installer.");

        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                0 => new SetupResult(SetupOutcome.Ok),
                ExitSlotOccupied => new SetupResult(SetupOutcome.SlotOccupied, LastLogLine()),
                _ => new SetupResult(SetupOutcome.Failed, LastLogLine() ?? $"Installer exited with {process.ExitCode}."),
            };
        }
    }

    private static string? LastLogLine()
    {
        try
        {
            return File.Exists(LogPath) ? File.ReadLines(LogPath).LastOrDefault() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
