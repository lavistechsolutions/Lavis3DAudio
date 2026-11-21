namespace Lavis3DAudio.Audio;

internal sealed class Biquad
{
    double b0 = 1, b1, b2, a1, a2, z1, z2;

    public void SetPeaking(double fs, double freq, double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w = 2 * Math.PI * freq / fs;
        double cs = Math.Cos(w);
        double alpha = Math.Sin(w) / (2 * q);
        double a0 = 1 + alpha / a;
        b0 = (1 + alpha * a) / a0;
        b1 = -2 * cs / a0;
        b2 = (1 - alpha * a) / a0;
        a1 = -2 * cs / a0;
        a2 = (1 - alpha / a) / a0;
    }

    public float Process(float x)
    {
        double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return (float)y;
    }
}

internal sealed class OnePole
{
    readonly float a;
    float y;

    public OnePole(float cutoffHz, int fs) => a = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / fs);

    public float Process(float x)
    {
        y += a * (x - y) + 1e-20f;
        return y;
    }
}

/// <summary>Circular buffer with fractional (linearly interpolated) reads.</summary>
internal sealed class DelayLine
{
    readonly float[] buf;
    int idx;

    public DelayLine(int maxSamples) => buf = new float[maxSamples];

    public void Push(float x)
    {
        buf[idx] = x;
        idx = (idx + 1) % buf.Length;
    }

    /// <summary>Returns the sample written <paramref name="delay"/> samples ago (0 = latest).</summary>
    public float Read(float delay)
    {
        float p = idx - 1 - delay;
        while (p < 0) p += buf.Length;
        int i0 = (int)p;
        float frac = p - i0;
        // Wrapping a tiny negative position can round up to exactly buf.Length in float precision.
        if (i0 >= buf.Length) i0 -= buf.Length;
        int i1 = (i0 + 1) % buf.Length;
        return buf[i0] * (1 - frac) + buf[i1] * frac;
    }
}

internal sealed class Allpass
{
    readonly float[] buf;
    readonly float g;
    int idx;

    public Allpass(int length, float gain)
    {
        buf = new float[Math.Max(1, length)];
        g = gain;
    }

    public float Process(float x)
    {
        float d = buf[idx];
        float y = -g * x + d;
        buf[idx] = x + g * y + 1e-20f;
        idx = (idx + 1) % buf.Length;
        return y;
    }
}

internal sealed class Comb
{
    readonly float[] buf;
    readonly float feedback, damp;
    float store;
    int idx;

    public Comb(int length, float feedback, float damp)
    {
        buf = new float[Math.Max(1, length)];
        this.feedback = feedback;
        this.damp = damp;
    }

    public float Process(float x)
    {
        float o = buf[idx];
        store = o * (1 - damp) + store * damp + 1e-20f;
        buf[idx] = x + store * feedback;
        idx = (idx + 1) % buf.Length;
        return o;
    }
}

/// <summary>Small Freeverb-style stereo room.</summary>
internal sealed class Reverb
{
    static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491 };
    static readonly int[] AllpassTunings = { 556, 441, 341 };
    const int StereoSpread = 23;

    readonly Comb[] combL, combR;
    readonly Allpass[] apL, apR;

    public Reverb(int fs)
    {
        double scale = fs / 44100.0;
        combL = CombTunings.Select(t => new Comb((int)(t * scale), 0.84f, 0.25f)).ToArray();
        combR = CombTunings.Select(t => new Comb((int)((t + StereoSpread) * scale), 0.84f, 0.25f)).ToArray();
        apL = AllpassTunings.Select(t => new Allpass((int)(t * scale), 0.5f)).ToArray();
        apR = AllpassTunings.Select(t => new Allpass((int)((t + StereoSpread) * scale), 0.5f)).ToArray();
    }

    public void Process(float input, out float left, out float right)
    {
        float x = input * 0.015f;
        left = right = 0;
        for (int i = 0; i < combL.Length; i++)
        {
            left += combL[i].Process(x);
            right += combR[i].Process(x);
        }
        for (int i = 0; i < apL.Length; i++)
        {
            left = apL[i].Process(left);
            right = apR[i].Process(right);
        }
        left *= 1.4f;
        right *= 1.4f;
    }
}
