# WaveGate

A soundboard overlay that mixes clips into your real microphone through a capture-side APO. No virtual cable is needed, and your default device stays the same.

## Layout

```
WaveGate/
├─ README.md
└─ src/
   ├─ WaveGate.Apo/                  C++ APO DLL, loaded by audiodg.exe (x64)
   │  ├─ WaveGate.Apo.vcxproj
   │  ├─ WaveGateApo.def             COM exports
   │  ├─ Guids.h                     APO CLSID
   │  ├─ SharedRing.h / .cpp         ring layout + lock-free reader (consumer)
   │  ├─ WaveGateApo.h / .cpp        CBaseAudioProcessingObject, APOProcess mixing
   │  └─ DllMain.cpp                 class factory, DllRegisterServer
   ├─ WaveGate.Audio/                C# library: decode, mix, sidetone, ring writer
   │  ├─ RingLayout.cs               mirror of SharedRing.h
   │  ├─ SharedRingWriter.cs         ring writer (producer)
   │  ├─ MicTapSampleProvider.cs     downmix + resample to the APO's rate -> ring
   │  └─ SoundEngine.cs              clip voices, mixer, WASAPI sidetone, Stop All
   ├─ WaveGate.Installer/            elevated CLI (requireAdministrator)
   │  ├─ Program.cs                  list | register | unregister | attach | detach | install | uninstall
   │  ├─ ApoAttachment.cs            FxProperties attach/detach + JSON backup
   │  ├─ RegistryOwnership.cs        take ownership / restore the original ACL
   │  └─ AudioService.cs             restart Audiosrv
   └─ WaveGate.App/                  WPF overlay -> WaveGate.exe (namespace WaveGate.Overlay)
      ├─ App.xaml(.cs)               wires config, engine, hotkeys, tray, overlay
      ├─ MainWindow.xaml(.cs)        always-on-top, never takes focus, clip grid, drag-and-drop
      ├─ Tray/TrayIcon.cs            show/hide, Stop all, Microphone attach/detach, Exit
      ├─ Hotkeys/                    Hotkey parsing, RegisterHotKey, capture dialog
      ├─ Setup/MicSetup.cs           runs WaveGate.Installer elevated (the only UAC prompt)
      └─ Config/                     %AppData%\WaveGate\board.json + clips\
```

## Using the overlay

- Drop MP3, WAV or OGG files onto the overlay. Each file is copied into `%AppData%\WaveGate\clips\`. Click a clip to play it.
- Right-click a clip to set or clear its hotkey, or to remove it from the board. Removing a clip leaves the copied file in `clips\`.
- The default global hotkeys are `Ctrl+Alt+W` to show or hide the overlay and `Ctrl+Alt+X` to stop all clips. You can change them in `board.json`.
- A hotkey must include Ctrl, Alt or Win. F1–F24 and numpad keys are the exception and may be used on their own.
- Tray icon > Microphone > Attach to … runs the installer once, behind a single UAC prompt. Detach restores the mic's original settings.
- The dot in the header shows the mic link:
  - grey: no mic set up
  - amber: attached, but waiting for an app to open the mic
  - green: live

## Data flow

```
clip files ─► Voice (decode → float → mixer rate → stereo) ─► MixingSampleProvider
                                                                    │
                                                     MicTapSampleProvider
                                     ┌──────────────────────────────┴───────────────┐
                              headphones (WasapiOut)               mono → APO rate → shared ring
                                                                                      │
                                                    audiodg.exe: APOProcess  mic + clip, clamped
```

- **Pacing:** the headphone device clock drives production. The APO keeps a 20 ms jitter cushion. If the producer gets more than 200 ms ahead, the APO resyncs to 40 ms. This absorbs clock drift between the two devices.
- **Mono ring:** the APO copies each sample to every mic channel, so the two sides can never disagree about frame alignment.
- **Multiple APO instances:** if more than one instance exists for the endpoint, they compete for a heartbeat lease. Only the holder reads from the ring.
- **Untrusted input:** the APO treats everything in shared memory as untrusted. Indices are masked, counts are bounded and NaN/Inf samples are replaced. A bad producer can cause glitches but not a crash.

## Build

- **APO:** needs Visual Studio 2022 with the "Desktop development with C++" workload, the "C++ ATL" component and a Windows SDK (built and verified with 10.0.22621). It links the same libraries as Microsoft's SysVAD SwapAPO sample (`AudioBaseProcessingObjectV140.lib`, `audiomediatypecrt.lib`, `AudioEng.lib`). The static CRT is used, so the DLL depends only on system DLLs.

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    src\WaveGate.Apo\WaveGate.Apo.vcxproj /p:Configuration=Release /p:Platform=x64
```
- **C#:** needs the .NET 8 SDK. The overlay expects `WaveGate.Installer.exe` and `WaveGateApo.dll` in its own folder, so build everything into one output folder:

```powershell
dotnet build src/WaveGate.Installer -c Release -o out
dotnet build src/WaveGate.App -c Release -o out
copy src\WaveGate.Apo\x64\Release\WaveGateApo.dll out\
out\WaveGate.exe
```

## Install from the command line (what the tray menu runs)

```powershell
# elevated
copy WaveGateApo.dll "C:\Program Files\WaveGate\"      # LOCAL SERVICE must be able to read it
WaveGate.Installer register "C:\Program Files\WaveGate\WaveGateApo.dll"
WaveGate.Installer list
WaveGate.Installer attach "{0.0.1.00000000}.{...}"     # add --replace if a vendor EFX is present
# undo
WaveGate.Installer detach "{0.0.1.00000000}.{...}"
WaveGate.Installer unregister "C:\Program Files\WaveGate\WaveGateApo.dll"
```

`attach` writes a backup to `%ProgramData%\WaveGate\backup\{endpoint}.json` before changing anything. The backup holds the original EFX value, the enhancements toggle and the key's security descriptor. `detach` restores all three and renames the backup to `*.restored-<timestamp>.json`.

## Known limits / open questions

- The APO uses the **EFX** slot (`{d04e05a6-…},7`). If a vendor driver already uses that slot, `--replace` disables the vendor effect while WaveGate is attached. Win11 CompositeFX chaining would avoid this but is not implemented yet.
- Unsigned APOs may be blocked on some systems or by some anti-cheat. Plan to code-sign the DLL before release.
- The shared ring grants read/write to Interactive Users. This means any local process in your session could inject audio into the mic while WaveGate is attached.
- The sidetone stream is rebuilt after mic setup, but not when the default headphone device changes or is unplugged. Restart WaveGate in that case.
- The tray uses a stock Windows icon until the project gets its own `.ico`.
- All four projects build cleanly, and the overlay launches. The APO has not yet been registered or loaded by audiodg, and nothing has been tested end to end.
