using NAudio.Wave;

namespace Lavis3DAudio.Audio;

/// <summary>Converts any channel layout to interleaved stereo float.</summary>
public sealed class StereoAdapter : ISampleProvider
{
    readonly ISampleProvider source;
    readonly int channels;
    float[] temp = Array.Empty<float>();

    public StereoAdapter(ISampleProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (channels == 2) return source.Read(buffer, offset, count);

        int frames = count / 2;
        int needed = frames * channels;
        if (temp.Length < needed) temp = new float[needed];
        int read = source.Read(temp, 0, needed);
        int framesRead = read / channels;

        for (int f = 0; f < framesRead; f++)
        {
            float l = temp[f * channels];
            float r = channels == 1 ? l : temp[f * channels + 1];
            buffer[offset + f * 2] = l;
            buffer[offset + f * 2 + 1] = r;
        }
        return framesRead * 2;
    }
}
