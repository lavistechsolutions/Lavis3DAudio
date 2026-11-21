using NAudio.Dsp;
using NAudio.Wave;

namespace Lavis3DAudio.Audio;

/// <summary>Pass-through provider that keeps the latest samples for the spectrum visualizer.</summary>
public sealed class SpectrumTap : ISampleProvider
{
    const int FftSize = 2048;
    const int FftOrder = 11;

    readonly ISampleProvider source;
    readonly float[] ring = new float[FftSize];
    readonly object gate = new();
    readonly Complex[] fft = new Complex[FftSize];
    int pos;
    bool failed;

    public SpectrumTap(ISampleProvider source) => this.source = source;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        if (failed) return n;
        try
        {
            lock (gate)
            {
                for (int i = 0; i < n; i += 2)
                {
                    ring[pos] = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f;
                    pos = (pos + 1) & (FftSize - 1);
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            ErrorLog.Write("Spectrum visualizer disabled after an error", ex);
        }
        return n;
    }

    /// <summary>Fills <paramref name="bands"/> with log-spaced levels in 0..1.</summary>
    public void GetBands(float[] bands, double minHz = 30, double maxHz = 16000)
    {
        lock (gate)
        {
            for (int i = 0; i < FftSize; i++)
            {
                fft[i].X = ring[(pos + i) & (FftSize - 1)] * (float)FastFourierTransform.HannWindow(i, FftSize);
                fft[i].Y = 0;
            }
        }
        FastFourierTransform.FFT(true, FftOrder, fft);

        double binHz = (double)WaveFormat.SampleRate / FftSize;
        maxHz = Math.Min(maxHz, WaveFormat.SampleRate / 2.0);
        for (int b = 0; b < bands.Length; b++)
        {
            double lo = minHz * Math.Pow(maxHz / minHz, (double)b / bands.Length);
            double hi = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / bands.Length);
            int i0 = Math.Max(1, (int)(lo / binHz));
            int i1 = Math.Max(i0, Math.Min(FftSize / 2 - 1, (int)(hi / binHz)));

            float peak = 0;
            for (int k = i0; k <= i1; k++)
                peak = MathF.Max(peak, MathF.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y));

            double db = 20 * Math.Log10(peak + 1e-9);
            bands[b] = (float)Math.Clamp((db + 80) / 70, 0, 1);
        }
    }
}
