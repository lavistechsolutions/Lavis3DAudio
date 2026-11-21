using NAudio.Wave;

namespace Lavis3DAudio.Audio;

/// <summary>Master volume + gain boost followed by a peak limiter so boosted audio never clips.</summary>
public sealed class BoosterProvider : ISampleProvider
{
    const float Ceiling = 0.97f;

    readonly ISampleProvider source;
    readonly DspSettings settings;
    readonly float release;
    float gain = -1, envelope = 1;
    float peakL, peakR;
    bool failed;

    public BoosterProvider(ISampleProvider source, DspSettings settings)
    {
        this.source = source;
        this.settings = settings;
        release = (float)Math.Exp(-1.0 / (0.2 * source.WaveFormat.SampleRate));
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public bool Limiting { get; private set; }

    public (float Left, float Right) TakePeaks()
    {
        var peaks = (peakL, peakR);
        peakL = peakR = 0;
        return peaks;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        if (!failed)
        {
            try
            {
                Process(buffer, offset, n);
                return n;
            }
            catch (Exception ex)
            {
                failed = true;
                ErrorLog.Write("Volume booster disabled after an error", ex);
            }
        }

        // Fallback: plain volume, never louder than full scale.
        float volume = settings.Volume;
        for (int i = 0; i < n; i++) buffer[offset + i] = Math.Clamp(buffer[offset + i] * volume, -1f, 1f);
        return n;
    }

    void Process(float[] buffer, int offset, int n)
    {
        float target = settings.Volume * MathF.Pow(10, settings.BoostDb / 20);
        if (gain < 0) gain = target;

        float pl = 0, pr = 0, minEnvelope = 1;
        for (int i = 0; i < n; i += 2)
        {
            gain += (target - gain) * 0.002f;
            float l = buffer[offset + i] * gain;
            float r = buffer[offset + i + 1] * gain;

            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float wanted = peak > Ceiling ? Ceiling / peak : 1f;
            envelope = wanted < envelope ? wanted : wanted + (envelope - wanted) * release;
            minEnvelope = MathF.Min(minEnvelope, envelope);

            l = Math.Clamp(l * envelope, -1f, 1f);
            r = Math.Clamp(r * envelope, -1f, 1f);
            buffer[offset + i] = l;
            buffer[offset + i + 1] = r;
            pl = MathF.Max(pl, MathF.Abs(l));
            pr = MathF.Max(pr, MathF.Abs(r));
        }

        peakL = MathF.Max(peakL, pl);
        peakR = MathF.Max(peakR, pr);
        Limiting = minEnvelope < 0.98f;
    }
}
