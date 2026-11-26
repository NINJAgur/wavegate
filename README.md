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
      ├─ App.xaml(.cs)               controller (implements IBoard) + shared dark theme
      ├─ IBoard.cs                   what the windows may ask of the app
      ├─ LibraryWindow.xaml(.cs)     full soundboard: categories, search, import, volumes
      ├─ ClipEditorWindow.xaml(.cs)  per-clip name, category, volume, retrigger, trim, hotkey
      ├─ MainWindow.xaml(.cs)        compact overlay: always-on-top, never takes focus
      ├─ Tray/TrayIcon.cs            show/hide, Stop all, Microphone attach/detach, Exit
      ├─ Hotkeys/                    Hotkey parsing, RegisterHotKey, capture dialog
      ├─ Setup/MicSetup.cs           runs WaveGate.Installer elevated (the only UAC prompt)
      └─ Config/                     %AppData%\WaveGate\board.json + clips\
```

## Using WaveGate

- **Library** (opens at startup; tray icon > Open library, or double-click the tray icon): the full soundboard.
  - Add sounds by dropping files or folders onto it, or with **Add sounds** / **Add folder**. MP3, WAV, OGG, MP4 and M4A work; for video only the audio track is used.
  - Every file is **copied** into `%AppData%\WaveGate\clips\`, so moving or deleting the original does not break the board. The board itself (names, categories, hotkeys, settings) lives in `%AppData%\WaveGate\board.json`.
  - Files inside a dropped folder go into a category named after the folder that contains them. Loose files go into the selected category.
  - Categories: **+ New category**, right-click to rename or delete (deleting keeps the sounds), drag a sound onto a category to move it.
  - Right-click a sound for Edit (name, category, volume, trim start/end with preview, what a second press does: restart / overlap / stop), hotkeys, favorite, or Remove from board. Removing leaves the copied file in `clips\`.
  - **Mic volume** sets how loud clips are for others; **Earphones** sets how loud you hear them.
- **Overlay**: a small always-on-top strip for in-game use that never takes focus. It shows ★ Favorites by default; right-click its header to show all sounds or one category instead. Files dropped on it land in what it is showing.
- The default global hotkeys are `Ctrl+Alt+W` to show or hide the overlay and `Ctrl+Alt+X` to stop all clips. You can change them in `board.json`.- A hotkey must include Ctrl, Alt or Win. F1–F24 and numpad keys are the exception and may be used on their own.
- Tray icon > Microphone > Attach to … runs the installer once, behind a single UAC prompt. Detach restores the mic's original settings.
- The dot in the overlay header and the library status bar shows the mic link:
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
- **Setup exe:** `.\build-setup.ps1 -Version 1.0.0` builds the APO, publishes both apps self-contained (no .NET needed on the target PC) and wraps them with [Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) into `dist\WaveGate-Setup-<version>.exe`. It installs to `Program Files\WaveGate`. The uninstaller runs `WaveGate.Installer uninstall-all` first, which detaches every microphone and restores its original settings.

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

`attach` writes a backup to `%ProgramData%\WaveGate\backup\{endpoint}.json` before changing anything. The backup holds the original values of every FX slot it touches, the enhancements toggle and the key's security descriptor. `detach` restores them and renames the backup to `*.restored-<timestamp>.json`.

## Known limits / open questions

- The APO goes in the legacy **LFX/GFX** slots (`{d04e05a6-…},1` and `,2`). Windows 11 never loaded it from EFX (`,7`) on a USB mic with no driver effects, even though the same DLL works when called directly. LFX/GFX are ignored whenever SFX/MFX/EFX are set, so attach clears those. If a vendor effect occupies any of these slots, `--replace` disables it while WaveGate is attached; detach restores it.
- Unsigned APOs may be blocked on some systems or by some anti-cheat. Plan to code-sign the DLL before release.
- The shared ring grants read/write to Interactive Users. This means any local process in your session could inject audio into the mic while WaveGate is attached.
- The sidetone stream is rebuilt after mic setup, but not when the default headphone device changes or is unplugged. Restart WaveGate in that case.
- The tray uses a stock Windows icon until the project gets its own `.ico`.
- Verified on Windows 11 23H2 with a HyperX Cloud III (USB, inbox driver): audiodg loads the APO, and a tone written to the ring came out of the mic capture at the expected level. The full overlay-to-mic path (clip playback through `SoundEngine`) has not been tested yet.
