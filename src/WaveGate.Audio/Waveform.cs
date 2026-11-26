using NAudio.Wave;

namespace WaveGate.Audio;

/// <param name="Peaks">Loudness per bucket, 0..1, normalised to the clip's loudest bucket.</param>
/// <param name="Length">Duration of the trimmed clip.</param>
public sealed record WaveformInfo(float[] Peaks, TimeSpan Length);

public static class Waveform
{
    /// <summary>Decodes the trimmed clip once and returns its loudness shape for the tile.</summary>
    public static WaveformInfo Analyze(string path, PlayOptions options, int buckets = 96)
    {
        using var reader = SoundEngine.OpenReader(path);
        var total = reader.TotalTime.TotalSeconds;
        var start = Math.Clamp(options.TrimStart, 0, total);
        var end = Math.Clamp(options.TrimEnd ?? total, start, total);
        if (start > 0) reader.CurrentTime = TimeSpan.FromSeconds(start);

        var samples = reader.ToSampleProvider();
        int channels = samples.WaveFormat.Channels;
        long frames = Math.Max(1, (long)((end - start) * samples.WaveFormat.SampleRate));
        var sums = new double[buckets];
        var counts = new int[buckets];
        var buffer = new float[channels * 4096];

        long frame = 0;
        int read;
        while (frame < frames && (read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channels <= read && frame < frames; i += channels, frame++)
            {
                int b = (int)(frame * buckets / frames);
                for (int c = 0; c < channels; c++) sums[b] += buffer[i + c] * buffer[i + c];
                counts[b] += channels;
            }
        }

        // RMS rather than peak: compressed clips peak everywhere and would draw as a flat block.
        var peaks = new float[buckets];
        for (int b = 0; b < buckets; b++) peaks[b] = counts[b] == 0 ? 0 : (float)Math.Sqrt(sums[b] / counts[b]);
        var max = peaks.Max();
        if (max > 0)
            for (int b = 0; b < buckets; b++) peaks[b] = MathF.Pow(peaks[b] / max, 0.8f);

        return new WaveformInfo(peaks, TimeSpan.FromSeconds(end - start));
    }
}
