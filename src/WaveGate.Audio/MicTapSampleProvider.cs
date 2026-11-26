using NAudio.Dsp;
using NAudio.Wave;

namespace WaveGate.Audio;

/// <summary>
/// Sits between the clip mixer and the headphone output. Every block the
/// headphones pull is also downmixed to mono, resampled to the APO's rate and
/// pushed into the shared ring, so the headphone clock paces the mic feed and
/// sidetone and mic stay in step. Clock drift between the two devices is
/// absorbed on the APO side, which resyncs if the ring runs too far ahead.
/// </summary>
public sealed class MicTapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SharedRingWriter _ring;
    private readonly int _channels;
    private readonly int _inRate;

    private WdlResampler? _resampler;
    private int _resamplerOutRate;
    private float[] _mono = [];
    private float[] _resampled = [];

    public MicTapSampleProvider(ISampleProvider source, SharedRingWriter ring)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Source must be IEEE float", nameof(source));
        _source = source;
        _ring = ring;
        _channels = source.WaveFormat.Channels;
        _inRate = source.WaveFormat.SampleRate;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float MicVolume { get; set; } = 1f;
    public float SidetoneVolume { get; set; } = 1f;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        if (_ring.IsOpen) PushToMic(buffer.AsSpan(offset, read));

        float sidetone = SidetoneVolume;
        if (sidetone != 1f)
        {
            for (int i = 0; i < read; i++) buffer[offset + i] *= sidetone;
        }
        return read;
    }

    private void PushToMic(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return;

        EnsureSize(ref _mono, frames);
        float gain = MicVolume / _channels;
        for (int f = 0, i = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < _channels; c++) sum += interleaved[i++];
            _mono[f] = sum * gain;
        }

        // Until an APO publishes its rate, write at our own rate; nobody is listening anyway.
        int outRate = _ring.SampleRate;
        if (outRate <= 0) outRate = _inRate;

        if (outRate == _inRate)
        {
            _ring.Write(_mono.AsSpan(0, frames));
            return;
        }

        if (_resampler is null || _resamplerOutRate != outRate)
        {
            _resampler = CreateResampler(_inRate, outRate);
            _resamplerOutRate = outRate;
        }

        // Feed mode: we push exactly `frames` input samples and take whatever comes out.
        int inNeeded = _resampler.ResamplePrepare(frames, 1, out float[] inBuffer, out int inOffset);
        int fed = Math.Min(inNeeded, frames);
        Array.Copy(_mono, 0, inBuffer, inOffset, fed);

        int maxOut = (int)((long)fed * outRate / _inRate) + 16;
        EnsureSize(ref _resampled, maxOut);
        int produced = _resampler.ResampleOut(_resampled, 0, fed, maxOut, 1);
        _ring.Write(_resampled.AsSpan(0, produced));
    }

    private static WdlResampler CreateResampler(int inRate, int outRate)
    {
        var r = new WdlResampler();
        r.SetMode(true, 2, false);
        r.SetFilterParms();
        r.SetFeedMode(true);
        r.SetRates(inRate, outRate);
        return r;
    }

    private static void EnsureSize(ref float[] buffer, int size)
    {
        if (buffer.Length < size) buffer = new float[size];
    }
}
