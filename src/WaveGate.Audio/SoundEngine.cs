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

    public float MicVolume { get => _tap.MicVolume; set => _tap.MicVolume = value; }
    public float SidetoneVolume { get => _tap.SidetoneVolume; set => _tap.SidetoneVolume = value; }

    public void Play(string path)
    {
        var voice = Voice.Open(path, _mixer.WaveFormat);
        lock (_voicesLock) _voices.Add(voice);
        _mixer.AddMixerInput(voice);
    }

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
        StopAll();
        _ring.Dispose(); // safe: the output thread has stopped calling the tap
    }

    /// <summary>One playing clip: file reader converted to the mixer's float/rate/stereo format.</summary>
    private sealed class Voice : ISampleProvider, IDisposable
    {
        private readonly WaveStream _reader;
        private readonly ISampleProvider _pipeline;

        private Voice(WaveStream reader, ISampleProvider pipeline)
        {
            _reader = reader;
            _pipeline = pipeline;
        }

        public WaveFormat WaveFormat => _pipeline.WaveFormat;

        public int Read(float[] buffer, int offset, int count) => _pipeline.Read(buffer, offset, count);

        public static Voice Open(string path, WaveFormat target)
        {
            WaveStream reader = Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? new VorbisWaveReader(path)
                : new AudioFileReader(path); // WAV, MP3; MP4/M4A go through Media Foundation (audio track only)

            try
            {
                ISampleProvider p = reader.ToSampleProvider();

                if (p.WaveFormat.SampleRate != target.SampleRate)
                    p = new WdlResamplingSampleProvider(p, target.SampleRate);

                p = p.WaveFormat.Channels switch
                {
                    1 => new MonoToStereoSampleProvider(p),
                    2 => p,
                    _ => new MultiplexingSampleProvider([p], 2), // keep front L/R of multichannel files
                };

                return new Voice(reader, p);
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
