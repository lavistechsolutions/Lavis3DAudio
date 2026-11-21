namespace Lavis3DAudio.Audio;

/// <summary>Live parameters shared between the UI thread and the audio thread.</summary>
public sealed class DspSettings
{
    public volatile bool SurroundEnabled = true;
    public volatile bool HeadphoneMode = true;
    public volatile float Intensity = 0.7f;   // 0..1
    public volatile float Ambience = 0.25f;   // 0..1
    public volatile float OrbitSpeed;         // rotations per second, 0 = off

    public volatile float Volume = 0.8f;      // 0..1
    public volatile float BoostDb;            // 0..+12 dB

    public volatile bool EqEnabled = true;
    public readonly float[] EqGains = new float[10];
    public int EqVersion;
}
