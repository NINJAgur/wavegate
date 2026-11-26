using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace WaveGate.Installer;

public sealed record CaptureDevice(string Id, string Name);

public sealed class SlotOccupiedException(string message) : InvalidOperationException(message);

/// <summary>
/// Attaches/detaches the WaveGate APO to one capture endpoint's FxProperties.
/// Everything touched is backed up to %ProgramData%\WaveGate\backup before the first write.
/// </summary>
public static class ApoAttachment
{
    // Must match CLSID_WaveGateApo in src/WaveGate.Apo/Guids.h.
    public const string ApoClsid = "{6E56FA97-0CB5-4A10-B2A8-5B83999A1A67}";

    // Legacy LFX/GFX (PreMix/PostMix) slots. The engine ignored our EFX on a USB mic with no
    // driver FX; LFX/GFX is what Equalizer APO settled on because it loads everywhere.
    private const string LfxValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
    private const string GfxValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2";

    // SFX/MFX/EFX: if any is present, Windows ignores LFX/GFX, so they must be absent.
    private static readonly string[] ModernValues =
    [
        "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5",
        "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6",
        "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7",
    ];

    // PKEY_AudioEndpoint_Disable_SysFx: 1 = "Audio enhancements" off, which would bypass the APO.
    private const string DisableSysFxValue = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";

    private const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

    private static readonly string BackupDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveGate", "backup");

    public static IReadOnlyList<CaptureDevice> ListCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new CaptureDevice(d.ID, d.FriendlyName))
            .ToList();
    }

    public static void Attach(string deviceId, bool replaceExisting)
    {
        var endpointPath = EndpointPath(deviceId);
        var fxPath = endpointPath + @"\FxProperties";
        var backupFile = BackupFile(endpointPath);

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var endpoint = hklm.OpenSubKey(endpointPath))
        {
            if (endpoint is null) throw new InvalidOperationException($"Capture endpoint not found: {deviceId}");
        }

        bool fxExisted;
        using (var fx = hklm.OpenSubKey(fxPath)) fxExisted = fx is not null;

        // If FxProperties is missing we must create it, which needs write access on the parent.
        var aclPath = fxExisted ? fxPath : endpointPath;

        using (var fx = hklm.OpenSubKey(fxPath))
        {
            var foreign = new[] { LfxValue, GfxValue }.Concat(ModernValues)
                .Select(name => fx?.GetValue(name) as string)
                .FirstOrDefault(v => !string.IsNullOrEmpty(v) && !IsOurs(v));
            if (foreign is not null && !replaceExisting)
                throw new SlotOccupiedException(
                    $"An effect slot on this mic is already used by {foreign} (likely a vendor effect). " +
                    "Re-run with --replace to override it; the original is restored on detach.");

            // An existing backup means an earlier attach did not finish (or used other slots). It
            // holds the true original state, so keep it and only add values it lacks: we never
            // wrote those, so their current state is still the original.
            var backup = File.Exists(backupFile)
                ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile), JsonOptions)!
                : new Backup(deviceId, aclPath, RegistryOwnership.GetSddl(hklm, aclPath), []);
            foreach (var name in TouchedValues.Where(n => backup.Values.All(v => v.Name != n)))
                backup.Values.Add(Snapshot(fx, name));

            // Persist the backup before touching anything so a crash mid-way is recoverable.
            Directory.CreateDirectory(BackupDir);
            File.WriteAllText(backupFile, JsonSerializer.Serialize(backup, JsonOptions));
        }

        RegistryOwnership.EnsureWritable(hklm, aclPath);

        using (var fx = hklm.CreateSubKey(fxPath, writable: true))
        {
            foreach (var name in ModernValues) fx.DeleteValue(name, throwOnMissingValue: false);
            fx.SetValue(LfxValue, ApoClsid, RegistryValueKind.String);
            fx.SetValue(GfxValue, ApoClsid, RegistryValueKind.String);
            fx.SetValue(DisableSysFxValue, 0, RegistryValueKind.DWord);
        }
    }

    private static readonly string[] TouchedValues = [LfxValue, GfxValue, .. ModernValues, DisableSysFxValue];

    public static void Detach(string deviceId)
    {
        var endpointPath = EndpointPath(deviceId);
        var fxPath = endpointPath + @"\FxProperties";
        var backupFile = BackupFile(endpointPath);

        if (!File.Exists(backupFile))
            throw new InvalidOperationException($"No backup found for {deviceId}; nothing to detach.");

        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile), JsonOptions)
            ?? throw new InvalidOperationException($"Backup file is unreadable: {backupFile}");

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        using (var fx = hklm.OpenSubKey(fxPath, writable: true))
        {
            if (fx is not null)
            {
                foreach (var value in backup.Values)
                    RestoreValue(fx, value);
            }
        }

        RegistryOwnership.Restore(hklm, backup.AclKeyPath, backup.OriginalSddl);

        // Keep the backup as an audit trail rather than deleting it.
        File.Move(backupFile, Path.ChangeExtension(backupFile, $".restored-{DateTime.Now:yyyyMMddHHmmss}.json"));
    }

    private static void RestoreValue(RegistryKey fx, ValueBackup value)
    {
        if (value.Kind is null)
        {
            // Value did not exist before; remove only what we wrote.
            if (value.Name == DisableSysFxValue || IsOurs(fx.GetValue(value.Name)))
                fx.DeleteValue(value.Name, throwOnMissingValue: false);
            return;
        }

        object data = value.Kind == RegistryValueKind.DWord ? value.DwordData!.Value : value.StringData!;
        fx.SetValue(value.Name, data, value.Kind.Value);
    }

    private static ValueBackup Snapshot(RegistryKey? fx, string name)
    {
        if (fx?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data)
            return new ValueBackup(name, null, null, null);

        var kind = fx.GetValueKind(name);
        return kind switch
        {
            RegistryValueKind.DWord => new ValueBackup(name, kind, null, (int)data),
            RegistryValueKind.String or RegistryValueKind.ExpandString => new ValueBackup(name, kind, (string)data, null),
            _ => throw new InvalidOperationException($"Unexpected registry type {kind} for {name}; refusing to modify."),
        };
    }

    private static bool IsOurs(object? value) =>
        value is string s && string.Equals(s, ApoClsid, StringComparison.OrdinalIgnoreCase);

    private static string EndpointPath(string deviceId)
    {
        // Capture IDs look like "{0.0.1.00000000}.{endpoint-guid}".
        var guidPart = deviceId[(deviceId.LastIndexOf('.') + 1)..];
        if (!deviceId.StartsWith("{0.0.1.", StringComparison.Ordinal) || !Guid.TryParse(guidPart, out var guid))
            throw new ArgumentException($"Not a capture endpoint ID: {deviceId}", nameof(deviceId));
        return $@"{CaptureRoot}\{guid:B}";
    }

    private static string BackupFile(string endpointPath) =>
        Path.Combine(BackupDir, Path.GetFileName(endpointPath) + ".json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record ValueBackup(string Name, RegistryValueKind? Kind, string? StringData, int? DwordData);

    private sealed record Backup(string DeviceId, string AclKeyPath, string OriginalSddl, List<ValueBackup> Values);
}
