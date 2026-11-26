using System.Diagnostics;
using WaveGate.Installer;

// Elevated CLI invoked by the overlay via ShellExecute "runas". Console output
// is this tool's user interface; the overlay reads the exit code, and errors are
// also appended to %ProgramData%\WaveGate\installer.log because an elevated
// process's console cannot be captured by its non-elevated parent.
//   list
//   register   <path\to\WaveGateApo.dll>
//   unregister <path\to\WaveGateApo.dll>
//   attach     <capture-device-id> [--replace]
//   detach     <capture-device-id>
//   install    <path\to\WaveGateApo.dll> <capture-device-id> [--replace]   (copy + register + attach)
//   uninstall  <capture-device-id>                                        (detach + unregister)
//   uninstall-all                                                        (detach every mic + unregister; used by the app uninstaller)

const int Ok = 0, Failed = 1, Usage = 2, SlotOccupied = 3;

var installedApo = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WaveGate", "WaveGateApo.dll");

if (args.Length == 0) return PrintUsage();

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "list":
            foreach (var d in ApoAttachment.ListCaptureDevices())
                Console.WriteLine($"{d.Id}\t{d.Name}");
            return Ok;

        case "register" when args.Length == 2:
            return RunRegsvr32(args[1], unregister: false);

        case "unregister" when args.Length == 2:
            return RunRegsvr32(args[1], unregister: true);

        case "attach" when args.Length >= 2:
            ApoAttachment.Attach(args[1], replaceExisting: args.Contains("--replace"));
            AudioService.Restart();
            Console.WriteLine("Attached. Audio service restarted.");
            return Ok;

        case "detach" when args.Length == 2:
            ApoAttachment.Detach(args[1]);
            AudioService.Restart();
            Console.WriteLine("Detached. Original settings restored, audio service restarted.");
            return Ok;

        case "switch" when args.Length >= 3:
            // New mic first: if its slot is taken nothing has changed yet, and the old mic keeps working.
            ApoAttachment.Attach(args[2], replaceExisting: args.Contains("--replace"));
            ApoAttachment.Detach(args[1]);
            AudioService.Restart();
            Console.WriteLine("Moved to the new microphone. Audio service restarted.");
            return Ok;

        case "install" when args.Length >= 3:
            CopyApo(args[1], installedApo);
            if (RunRegsvr32(installedApo, unregister: false) != Ok)
                throw new InvalidOperationException("regsvr32 failed to register the APO.");
            ApoAttachment.Attach(args[2], replaceExisting: args.Contains("--replace"));
            AudioService.Restart();
            Console.WriteLine("Installed and attached. Audio service restarted.");
            return Ok;

        case "uninstall" when args.Length == 2:
            ApoAttachment.Detach(args[1]);
            AudioService.Restart();
            // The DLL file itself is left in place; audiodg may still have it mapped.
            if (File.Exists(installedApo)) RunRegsvr32(installedApo, unregister: true);
            Console.WriteLine("Detached and unregistered. Audio service restarted.");
            return Ok;

        case "uninstall-all":
            // Run by the app's uninstaller: a mic left pointing at a deleted DLL would lose its effects chain.
            var attached = ApoAttachment.AttachedDeviceIds();
            foreach (var id in attached) ApoAttachment.Detach(id);
            if (attached.Count > 0) AudioService.Restart();
            if (File.Exists(installedApo)) RunRegsvr32(installedApo, unregister: true);
            Console.WriteLine($"Detached from {attached.Count} microphone(s) and unregistered.");
            return Ok;
        default:
            return PrintUsage();
    }
}
catch (SlotOccupiedException e)
{
    Report(e.Message);
    return SlotOccupied;
}
catch (Exception e)
{
    Report(e.Message);
    return Failed;
}

static void CopyApo(string source, string destination)
{
    var src = Path.GetFullPath(source);
    if (!File.Exists(src)) throw new FileNotFoundException("APO DLL not found", src);

    // Program Files: readable by LOCAL SERVICE (audiodg), writable only by admins.
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(src)))
        return;

    try
    {
        File.Copy(src, destination, overwrite: true);
    }
    catch (IOException)
    {
        throw new InvalidOperationException(
            "The installed APO is in use by the audio engine. Detach WaveGate from the microphone first, then retry.");
    }
}

static void Report(string message)
{
    Console.Error.WriteLine($"error: {message}");
    try
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WaveGate", "installer.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} error: {message}{Environment.NewLine}");
    }
    catch (IOException)
    {
        // Logging is best effort; the exit code still tells the overlay what happened.
    }
}

static int RunRegsvr32(string dllPath, bool unregister)
{
    var full = Path.GetFullPath(dllPath);
    if (!File.Exists(full)) throw new FileNotFoundException("APO DLL not found", full);

    var psi = new ProcessStartInfo("regsvr32.exe") { UseShellExecute = false };
    psi.ArgumentList.Add("/s");
    if (unregister) psi.ArgumentList.Add("/u");
    psi.ArgumentList.Add(full);

    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) Console.Error.WriteLine($"regsvr32 exited with {p.ExitCode}");
    return p.ExitCode == 0 ? Ok : Failed;
}

static int PrintUsage()
{
    Console.Error.WriteLine("usage: WaveGate.Installer list | register <dll> | unregister <dll> | attach <id> [--replace] | " +
                            "detach <id> | switch <old id> <new id> [--replace] | install <dll> <id> [--replace] | uninstall <id> | uninstall-all");
    return Usage;
}
