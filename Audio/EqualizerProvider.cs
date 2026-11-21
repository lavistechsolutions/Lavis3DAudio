using NAudio.Wave;

namespace Lavis3DAudio.Audio;

public sealed class EqualizerProvider : ISampleProvider
{
    public static readonly float[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    readonly ISampleProvider source;
    readonly DspSettings settings;
    readonly Biquad[] left = Frequencies.Select(_ => new Biquad()).ToArray();
    readonly Biquad[] right = Frequencies.Select(_ => new Biquad()).ToArray();
    int version = -1;
    bool active, failed;

    public EqualizerProvider(ISampleProvider source, DspSettings settings)
    {
        this.source = source;
        this.settings = settings;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        if (failed) return n;
        try
        {
            Process(buffer, offset, n);
        }
        catch (Exception ex)
        {
            failed = true;
            ErrorLog.Write("Equalizer disabled after an error", ex);
        }
        return n;
    }

    void Process(float[] buffer, int offset, int n)
    {
        if (!settings.EqEnabled) return;
        if (version != Volatile.Read(ref settings.EqVersion)) UpdateFilters();
        if (!active) return;

        for (int i = 0; i < n; i += 2)
        {
            float l = buffer[offset + i], r = buffer[offset + i + 1];
            for (int b = 0; b < left.Length; b++)
            {
                l = left[b].Process(l);
                r = right[b].Process(r);
            }
            buffer[offset + i] = l;
            buffer[offset + i + 1] = r;
        }
    }

    void UpdateFilters()
    {
        version = Volatile.Read(ref settings.EqVersion);
        int fs = WaveFormat.SampleRate;
        active = false;
        for (int b = 0; b < Frequencies.Length; b++)
        {
            float gain = settings.EqGains[b];
            float freq = Math.Min(Frequencies[b], fs * 0.45f);
            left[b].SetPeaking(fs, freq, 1.1, gain);
            right[b].SetPeaking(fs, freq, 1.1, gain);
            if (gain != 0) active = true;
        }
    }
}
