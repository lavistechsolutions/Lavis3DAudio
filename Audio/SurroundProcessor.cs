using NAudio.Wave;

namespace Lavis3DAudio.Audio;

/// <summary>
/// Virtual 3D surround rendered with Valve's Steam Audio HRTF: the stereo image is placed on virtual front
/// speakers, decorrelated side content on virtual rear speakers, and the centre image can orbit the listener.
/// Adds one Steam Audio frame of latency.
/// </summary>
public sealed unsafe class SurroundProcessor : ISampleProvider, IDisposable
{
    const int FrameSize = 256;
    const float Smoothing = 0.001f;
    const float RearAzimuth = 115f;

    readonly ISampleProvider source;
    readonly DspSettings settings;
    readonly int fs;
    readonly object gate = new();

    IntPtr context, hrtf;
    readonly IntPtr[] effects = new IntPtr[5];
    readonly NativeAudioBuffer[] inputs = new NativeAudioBuffer[5];
    readonly NativeAudioBuffer? binauralOut;
    const int FrontL = 0, FrontR = 1, Orbit = 2, RearL = 3, RearR = 4;

    readonly float[] inL = new float[FrameSize], inR = new float[FrameSize];
    readonly float[] dryL = new float[FrameSize], dryR = new float[FrameSize];
    readonly float[] wetL = new float[FrameSize], wetR = new float[FrameSize];
    int pos;

    readonly DelayLine rearDelayL, rearDelayR;
    readonly OnePole rearLpL, rearLpR;
    readonly Allpass[] decorL, decorR;
    readonly Reverb reverb;
    readonly float rearSamplesL, rearSamplesR;

    float mix, intensity, ambience, orbitMix, phase;
    bool bypassed, failed, disposed;

    public SurroundProcessor(ISampleProvider source, DspSettings settings)
    {
        this.source = source;
        this.settings = settings;
        fs = source.WaveFormat.SampleRate;

        rearDelayL = new DelayLine(fs / 10);
        rearDelayR = new DelayLine(fs / 10);
        rearLpL = new OnePole(4500, fs);
        rearLpR = new OnePole(4500, fs);
        decorL = new[] { new Allpass((int)(0.0047 * fs), 0.6f), new Allpass((int)(0.0113 * fs), 0.5f) };
        decorR = new[] { new Allpass((int)(0.0053 * fs), 0.6f), new Allpass((int)(0.0127 * fs), 0.5f) };
        reverb = new Reverb(fs);
        rearSamplesL = 0.012f * fs;
        rearSamplesR = 0.0145f * fs;

        mix = settings.SurroundEnabled ? 1 : 0;
        intensity = settings.Intensity;
        ambience = settings.Ambience;

        try
        {
            var contextSettings = new Phonon.ContextSettings { Version = Phonon.Version, SimdLevel = Phonon.SimdLevelAvx2 };
            Phonon.Check(Phonon.iplContextCreate(ref contextSettings, out context), "context creation");

            var audio = new Phonon.AudioSettings { SamplingRate = fs, FrameSize = FrameSize };
            var hrtfSettings = new Phonon.HrtfSettings { Type = Phonon.HrtfTypeDefault, Volume = 1, NormType = Phonon.HrtfNormRms };
            Phonon.Check(Phonon.iplHRTFCreate(context, ref audio, ref hrtfSettings, out hrtf), "HRTF creation");

            var effectSettings = new Phonon.BinauralEffectSettings { Hrtf = hrtf };
            for (int i = 0; i < effects.Length; i++)
            {
                Phonon.Check(Phonon.iplBinauralEffectCreate(context, ref audio, ref effectSettings, out effects[i]), "binaural effect creation");
                inputs[i] = new NativeAudioBuffer(1, FrameSize);
            }
            binauralOut = new NativeAudioBuffer(2, FrameSize);
        }
        catch (Exception ex)
        {
            failed = true;
            ErrorLog.Write("Steam Audio could not start; 3D surround is disabled", ex);
            Release();
        }
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        lock (gate)
        {
            if (failed || disposed) return n;
            try
            {
                Process(buffer, offset, n);
            }
            catch (Exception ex)
            {
                failed = true;
                ErrorLog.Write("3D surround disabled after an error", ex);
            }
        }
        return n;
    }

    void Process(float[] buffer, int offset, int n)
    {
        float mixTarget = settings.SurroundEnabled ? 1 : 0;

        for (int i = 0; i < n; i += 2)
        {
            float l = buffer[offset + i], r = buffer[offset + i + 1];
            mix += (mixTarget - mix) * Smoothing;

            // Wet output is one frame behind, so the dry signal is delayed by the same amount.
            float dl = dryL[pos], dr = dryR[pos];
            buffer[offset + i] = dl + (wetL[pos] - dl) * mix;
            buffer[offset + i + 1] = dr + (wetR[pos] - dr) * mix;
            dryL[pos] = inL[pos] = l;
            dryR[pos] = inR[pos] = r;

            if (++pos == FrameSize)
            {
                pos = 0;
                bool idle = mixTarget == 0 && mix < 1e-4f;
                if (!idle) RenderFrame();
                else if (!bypassed)
                {
                    bypassed = true;
                    Array.Clear(wetL);
                    Array.Clear(wetR);
                }
            }
        }
    }

    void RenderFrame()
    {
        if (bypassed)
        {
            foreach (var effect in effects) Phonon.iplBinauralEffectReset(effect);
            bypassed = false;
        }

        // Per-frame equivalent of the per-sample smoothing coefficient.
        float k = 1 - MathF.Pow(1 - Smoothing, FrameSize);
        intensity += (settings.Intensity - intensity) * k;
        ambience += (settings.Ambience - ambience) * k;
        float speed = settings.OrbitSpeed;
        orbitMix += ((speed > 0.001f ? 1 : 0) - orbitMix) * k;
        bool headphones = settings.HeadphoneMode;

        float width = 1 + intensity * (headphones ? 0.3f : 0.8f);
        float rearGain = 0.55f * intensity;
        float makeup = 1f / (1f + intensity * 0.45f);
        float m = orbitMix;

        float* fl = inputs[FrontL][0], fr = inputs[FrontR][0], orbit = inputs[Orbit][0];
        float* rl = inputs[RearL][0], rr = inputs[RearR][0];
        for (int s = 0; s < FrameSize; s++)
        {
            float mid = (inL[s] + inR[s]) * 0.5f;
            float side = (inL[s] - inR[s]) * 0.5f * width;

            // The orbiting source takes over the centre image from the front pair.
            fl[s] = mid * (1 - m) + side;
            fr[s] = mid * (1 - m) - side;
            orbit[s] = mid * m;

            rearDelayL.Push(decorL[1].Process(decorL[0].Process(side)));
            rearDelayR.Push(decorR[1].Process(decorR[0].Process(-side)));
            rl[s] = rearLpL.Process(rearDelayL.Read(rearSamplesL)) * rearGain;
            rr[s] = rearLpR.Process(rearDelayR.Read(rearSamplesR)) * rearGain;

            reverb.Process(mid, out float revL, out float revR);
            wetL[s] = revL * ambience * makeup;
            wetR[s] = revR * ambience * makeup;
        }

        phase += 2 * MathF.PI * speed * FrameSize / fs;
        if (phase > 2 * MathF.PI) phase -= 2 * MathF.PI;

        float front = 20f + 25f * intensity;
        float blend = headphones ? 1f : 0.5f;
        Render(FrontL, Direction(-front), blend, makeup);
        Render(FrontR, Direction(front), blend, makeup);
        Render(Orbit, Direction(phase * 180f / MathF.PI), blend, makeup);
        Render(RearL, Direction(-RearAzimuth), blend, makeup);
        Render(RearR, Direction(RearAzimuth), blend, makeup);
    }

    void Render(int index, Phonon.Vector3 direction, float blend, float gain)
    {
        var p = new Phonon.BinauralEffectParams
        {
            Direction = direction,
            Interpolation = Phonon.InterpolationBilinear,
            SpatialBlend = blend,
            Hrtf = hrtf,
        };
        Phonon.iplBinauralEffectApply(effects[index], ref p, ref inputs[index].Buffer, ref binauralOut!.Buffer);
        float* l = binauralOut[0], r = binauralOut[1];
        for (int s = 0; s < FrameSize; s++)
        {
            wetL[s] += l[s] * gain;
            wetR[s] += r[s] * gain;
        }
    }

    /// <summary>Unit vector on the horizontal plane; 0° is straight ahead, positive is to the listener's right.</summary>
    static Phonon.Vector3 Direction(float azimuthDegrees)
    {
        float a = azimuthDegrees * MathF.PI / 180f;
        return new Phonon.Vector3(MathF.Sin(a), 0, -MathF.Cos(a));
    }

    void Release()
    {
        for (int i = 0; i < effects.Length; i++)
            if (effects[i] != IntPtr.Zero) Phonon.iplBinauralEffectRelease(ref effects[i]);
        foreach (var input in inputs) input?.Dispose();
        binauralOut?.Dispose();
        if (hrtf != IntPtr.Zero) Phonon.iplHRTFRelease(ref hrtf);
        if (context != IntPtr.Zero) Phonon.iplContextRelease(ref context);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { Release(); }
            catch (Exception ex) { ErrorLog.Write("Releasing Steam Audio", ex); }
        }
    }
}
