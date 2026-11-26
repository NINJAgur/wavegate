using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveGate.Audio;

/// <param name="ProcessId">The process that owns the audio session.</param>
/// <param name="Name">Friendly name for the UI.</param>
public sealed record AudioApp(int ProcessId, string Name)
{
    public override string ToString() => Name; // what list controls display
}

/// <summary>
/// Captures what one app plays (and its child processes, e.g. a browser's audio service)
/// using Windows' process loopback (Windows 10 2004+). Nothing else on the PC is captured,
/// so voices from Discord or the clips' own sidetone never loop back into the mic.
/// </summary>
public sealed class AppAudioCapture : IDisposable
{
    private const long BufferDuration = 200 * 10_000; // 200 ms in 100 ns units
    private static readonly TimeSpan MaxLag = TimeSpan.FromMilliseconds(250);

    private readonly AudioClient _client;
    private readonly BufferedWaveProvider _buffer;
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    private AppAudioCapture(AudioClient client, WaveFormat format)
    {
        _client = client;
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        Output = new LagGuard(_buffer).ToSampleProvider();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WaveGate app capture" };
    }

    /// <summary>Stereo float at the requested rate; silence while the app is quiet.</summary>
    public ISampleProvider Output { get; }

    /// <summary>Apps with an audio session on the default output device, excluding this process.</summary>
    public static IReadOnlyList<AudioApp> ListApps()
    {
        var self = Environment.ProcessId;
        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        using var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            int pid = (int)session.GetProcessID;
            if (pid == 0 || pid == self || session.IsSystemSoundsSession) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                var name = Describe(process);
                apps.TryAdd(name, new AudioApp(pid, name));
            }
            catch (ArgumentException) { } // exited since the session list was taken
        }
        return [.. apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Describe(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch (System.ComponentModel.Win32Exception) { } // elevated or protected process
        return process.ProcessName;
    }

    public static AppAudioCapture Start(int processId, int sampleRate)
    {
        var format = new WaveFormat(sampleRate, 16, 2);
        var client = Activate(processId);
        try
        {
            // Process loopback has no mix format; the engine converts to whatever PCM format we ask for.
            const AudioClientStreamFlags flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
                                                 AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration, 0, format, Guid.Empty);
            var capture = new AppAudioCapture(client, format);
            client.SetEventHandle(capture._ready.SafeWaitHandle.DangerousGetHandle());
            client.Start();
            capture._thread.Start();
            return capture;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void CaptureLoop()
    {
        var capture = _client.AudioCaptureClient;
        int blockAlign = _buffer.WaveFormat.BlockAlign;
        var bytes = new byte[blockAlign * 4800];
        while (!_stop)
        {
            _ready.WaitOne(100);
            int packet;
            while (!_stop && (packet = capture.GetNextPacketSize()) > 0)
            {
                var data = capture.GetBuffer(out int frames, out var bufferFlags);
                int size = frames * blockAlign;
                if (bytes.Length < size) bytes = new byte[size];
                if ((bufferFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(bytes, 0, size);
                else Marshal.Copy(data, bytes, 0, size);
                capture.ReleaseBuffer(frames);
                _buffer.AddSamples(bytes, 0, size);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        _thread.Join(500);
        try { _client.Stop(); } catch (COMException) { } // device already gone
        _client.Dispose();
        _ready.Dispose();
    }

    /// <summary>Drops the backlog if the app's clock runs ahead of ours, so the mic never lags behind it.</summary>
    private sealed class LagGuard(BufferedWaveProvider buffer) : IWaveProvider
    {
        public WaveFormat WaveFormat => buffer.WaveFormat;

        public int Read(byte[] data, int offset, int count)
        {
            if (buffer.BufferedDuration > MaxLag) buffer.ClearBuffer();
            return buffer.Read(data, offset, count);
        }
    }

    // ---- Activation ----------------------------------------------------------

    private static AudioClient Activate(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS: type = PROCESS_LOOPBACK, target pid, mode = INCLUDE_TARGET_PROCESS_TREE.
        var parameters = Marshal.AllocHGlobal(12);
        var variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, 1);
            Marshal.WriteInt32(parameters, 4, processId);
            Marshal.WriteInt32(parameters, 8, 0);

            // PROPVARIANT { VT_BLOB, BLOB { cbSize, pBlobData } } on x64.
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
            Marshal.WriteInt16(variant, 0, 65);
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 16, parameters);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualProcessLoopback, typeof(IAudioClient).GUID, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Audio capture did not start.");
            Marshal.ThrowExceptionForHR(handler.Result);
            return new AudioClient((IAudioClient)handler.Interface!);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    private const string VirtualProcessLoopback = "VAD\\Process_Loopback";

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateCompletionHandler completionHandler,
        out IActivateOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateCompletionHandler
    {
        void ActivateCompleted(IActivateOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // The completion runs on an MTA worker thread; IAgileObject tells COM no marshalling is needed.
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result;
        public object? Interface;

        public void ActivateCompleted(IActivateOperation operation)
        {
            operation.GetActivateResult(out Result, out Interface);
            Done.Set();
        }
    }
}
