using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WaveGate.Audio;

/// <summary>
/// Decodes clips, mixes them, plays the mix to the headphones (sidetone) and,
/// through <see cref="MicTapSampleProvider"/>, streams the same mix into the mic APO.
/// </summary>
public sealed class SoundEngine : IDisposable
{
    private readonly SharedRingWriter _ring = new();
    private readonly MixingSampleProvider _mixer;
    private readonly MicTapSampleProvider _tap;
    private readonly WasapiOut _output;
    private readonly Timer _reconnectTimer;
    private readonly object _voicesLock = new();
    private readonly List<Voice> _voices = [];

    /// <param name="sidetoneDevice">Headphones to monitor on; null = default render device.</param>
    public SoundEngine(MMDevice? sidetoneDevice = null)
    {
        sidetoneDevice ??= new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Mix at the headphones' native rate so WASAPI does no extra resampling on the sidetone path.
        int rate = sidetoneDevice.AudioClient.MixFormat.SampleRate;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2))
        {
            // Keep producing silence when idle so the ring stays continuously fed.
            ReadFully = true,
        };
        _mixer.MixerInputEnded += OnMixerInputEnded;

        _tap = new MicTapSampleProvider(_mixer, _ring);

        _output = new WasapiOut(sidetoneDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        _output.Init(_tap);
        _output.Play();

        // The APO only exists while something is capturing from the mic; keep trying to attach.
        _reconnectTimer = new Timer(_ => _ring.TryOpen(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public bool IsMicLinked => _ring.IsConsumerActive;

    private AppAudioCapture? _appCapture;

    /// <summary>Mixes one app's audio into the mic feed (not the headphones); null stops sharing.</summary>
    public void ShareAppAudio(int? processId)
    {
        var old = _appCapture;
        _appCapture = null;
        _tap.MicOnly = null;
        // The loopback client is created on an MTA thread and has no cross-apartment proxy,
        // so it must never be touched from the UI's STA thread: start and stop it on the pool.
        if (old is not null) Task.Run(old.Dispose).GetAwaiter().GetResult();
        if (processId is not { } pid) return;

        int rate = _mixer.WaveFormat.SampleRate;
        var capture = Task.Run(() => AppAudioCapture.Start(pid, rate)).GetAwaiter().GetResult();
        _appCapture = capture;
        _tap.MicOnly = capture.Output;
    }

    public float MicVolume { get => _tap.MicVolume; set => _tap.MicVolume = value; }
    public float SidetoneVolume { get => _tap.SidetoneVolume; set => _tap.SidetoneVolume = value; }

    /// <param name="key">Identifies the clip so it can be stopped or tracked; several voices may share one.</param>
    /// <param name="startAt">Where to start, as a fraction 0..1 of the trimmed length.</param>
    public void Play(Guid key, string path, PlayOptions options, double startAt = 0)
    {
        var voice = Voice.Open(key, path, options, _mixer.WaveFormat, startAt);
        lock (_voicesLock) _voices.Add(voice);
        _mixer.AddMixerInput(voice);
    }

    /// <summary>Restarts the clip's newest voice at <paramref name="fraction"/>, keeping its paused state; false if it isn't playing.</summary>
    public bool Seek(Guid key, double fraction)
    {
        Voice? current;
        lock (_voicesLock) current = _voices.LastOrDefault(v => v.Key == key);
        if (current is null) return false;

        // Reopening is simpler than seeking a live pipeline: the trim, resampler and progress all start fresh.
        var next = Voice.Open(key, current.Path, current.Options, _mixer.WaveFormat, fraction);
        next.Paused = current.Paused;
        Stop(key);
        lock (_voicesLock) _voices.Add(next);
        _mixer.AddMixerInput(next);
        return true;
    }

    public bool IsPlaying(Guid key)
    {
        lock (_voicesLock) return _voices.Any(v => v.Key == key);
    }

    /// <summary>True when the clip has voices and all of them are paused.</summary>
    public bool IsPaused(Guid key)
    {
        lock (_voicesLock) return _voices.Any(v => v.Key == key) && _voices.All(v => v.Key != key || v.Paused);
    }

    public void SetPaused(Guid key, bool paused)
    {
        lock (_voicesLock)
            foreach (var v in _voices.Where(v => v.Key == key)) v.Paused = paused;
    }

    public void Stop(Guid key)
    {
        Voice[] stopped;
        lock (_voicesLock)
        {
            stopped = _voices.Where(v => v.Key == key).ToArray();
            _voices.RemoveAll(v => v.Key == key);
        }
        // RemoveMixerInput takes the mixer's source lock, so no Read is inside the voice afterwards.
        foreach (var v in stopped)
        {
            _mixer.RemoveMixerInput(v);
            v.Dispose();
        }
    }

    /// <summary>Playback position 0..1 of the newest voice of each playing clip.</summary>
    public Dictionary<Guid, double> GetProgress()
    {
        var result = new Dictionary<Guid, double>();
        lock (_voicesLock)
            foreach (var v in _voices) result[v.Key] = v.Progress;
        return result;
    }

    public static TimeSpan GetDuration(string path)
    {
        using var reader = OpenReader(path);
        return reader.TotalTime;
    }

    internal static WaveStream OpenReader(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path); // WAV, MP3; MP4/M4A go through Media Foundation (audio track only)

    public void StopAll()
    {
        // MixingSampleProvider holds its source lock while reading, so once this returns
        // no audio-thread Read is still inside any removed voice and disposal is safe.
        _mixer.RemoveAllMixerInputs();

        Voice[] stopped;
        lock (_voicesLock)
        {
            stopped = [.. _voices];
            _voices.Clear();
        }
        foreach (var v in stopped) v.Dispose();
    }

    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider is not Voice voice) return;
        lock (_voicesLock) _voices.Remove(voice);
        voice.Dispose();
    }

    public void Dispose()
    {
        _reconnectTimer.Dispose();
        _output.Stop();
        _output.Dispose();
        ShareAppAudio(null);
        StopAll();
        _ring.Dispose(); // safe: the output thread has stopped calling the tap
    }

    /// <summary>One playing clip: file reader converted to the mixer's float/rate/stereo format.</summary>
    private sealed class Voice : ISampleProvider, IDisposable
    {
        private readonly WaveStream _reader;
        private readonly ISampleProvider _pipeline;
        private readonly long _totalSamples;
        private long _samplesRead;

        private Voice(Guid key, string path, PlayOptions options, WaveStream reader, ISampleProvider pipeline,
            long totalSamples, long alreadyPlayed)
        {
            Key = key;
            Path = path;
            Options = options;
            _reader = reader;
            _pipeline = pipeline;
            _totalSamples = Math.Max(1, totalSamples);
            _samplesRead = alreadyPlayed;
        }

        public Guid Key { get; }
        public string Path { get; }
        public PlayOptions Options { get; }

        public double Progress => Math.Min(1.0, Interlocked.Read(ref _samplesRead) / (double)_totalSamples);

        public WaveFormat WaveFormat => _pipeline.WaveFormat;

        public volatile bool Paused;

        public int Read(float[] buffer, int offset, int count)
        {
            // Silence rather than 0 while paused: returning 0 would make the mixer drop the voice.
            if (Paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            int read = _pipeline.Read(buffer, offset, count);
            Interlocked.Add(ref _samplesRead, read);
            return read;
        }

        public static Voice Open(Guid key, string path, PlayOptions options, WaveFormat target, double startAt)
        {
            var reader = OpenReader(path);

            try
            {
                var total = reader.TotalTime.TotalSeconds;
                var start = Math.Clamp(options.TrimStart, 0, total);
                var end = Math.Clamp(options.TrimEnd ?? total, start, total);
                var from = start + (end - start) * Math.Clamp(startAt, 0, 1);
                if (from > 0) reader.CurrentTime = TimeSpan.FromSeconds(from);

                ISampleProvider p = reader.ToSampleProvider();
                if (end < total) p = new OffsetSampleProvider(p) { Take = TimeSpan.FromSeconds(end - from) };
                if (options.Volume != 1f) p = new VolumeSampleProvider(p) { Volume = options.Volume };

                if (p.WaveFormat.SampleRate != target.SampleRate)
                    p = new WdlResamplingSampleProvider(p, target.SampleRate);

                p = p.WaveFormat.Channels switch
                {
                    1 => new MonoToStereoSampleProvider(p),
                    2 => p,
                    _ => new MultiplexingSampleProvider([p], 2), // keep front L/R of multichannel files
                };

                return new Voice(key, path, options, reader, p,
                    (long)((end - start) * target.SampleRate) * target.Channels,
                    (long)((from - start) * target.SampleRate) * target.Channels);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public void Dispose() => _reader.Dispose();
    }
}

/// <param name="Volume">Linear gain applied to this clip.</param>
/// <param name="TrimStart">Seconds into the file to start at.</param>
/// <param name="TrimEnd">Seconds into the file to stop at; null = end of file.</param>
public sealed record PlayOptions(float Volume = 1f, double TrimStart = 0, double? TrimEnd = null);
