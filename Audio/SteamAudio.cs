using System.Runtime.InteropServices;

namespace Lavis3DAudio.Audio;

/// <summary>Minimal bindings for Steam Audio's C API (phonon.dll 4.8.x).</summary>
internal static unsafe class Phonon
{
    const string Dll = "phonon.dll";

    public const uint Version = (4u << 16) | (8u << 8) | 1u;

    public const int SimdLevelAvx2 = 3;
    public const int HrtfTypeDefault = 0;
    public const int HrtfNormRms = 1;
    public const int InterpolationBilinear = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ContextSettings
    {
        public uint Version;
        public IntPtr LogCallback, AllocateCallback, FreeCallback;
        public int SimdLevel;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioSettings
    {
        public int SamplingRate;
        public int FrameSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HrtfSettings
    {
        public int Type;
        public IntPtr SofaFileName;
        public IntPtr SofaData;
        public int SofaDataSize;
        public float Volume;
        public int NormType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BinauralEffectSettings
    {
        public IntPtr Hrtf;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BinauralEffectParams
    {
        public Vector3 Direction;
        public int Interpolation;
        public float SpatialBlend;
        public IntPtr Hrtf;
        public float* PeakDelays;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public int NumChannels;
        public int NumSamples;
        public float** Data;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int iplContextCreate(ref ContextSettings settings, out IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplContextRelease(ref IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int iplHRTFCreate(IntPtr context, ref AudioSettings audioSettings, ref HrtfSettings hrtfSettings, out IntPtr hrtf);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplHRTFRelease(ref IntPtr hrtf);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int iplBinauralEffectCreate(IntPtr context, ref AudioSettings audioSettings, ref BinauralEffectSettings effectSettings, out IntPtr effect);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplBinauralEffectRelease(ref IntPtr effect);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplBinauralEffectReset(IntPtr effect);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int iplBinauralEffectApply(IntPtr effect, ref BinauralEffectParams parameters, ref AudioBuffer input, ref AudioBuffer output);

    public static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"Steam Audio {what} failed (IPLerror {status}).");
    }
}

/// <summary>A deinterleaved float buffer in native memory, usable as an <see cref="Phonon.AudioBuffer"/>.</summary>
internal sealed unsafe class NativeAudioBuffer : IDisposable
{
    public Phonon.AudioBuffer Buffer;

    public NativeAudioBuffer(int channels, int samples)
    {
        Buffer.NumChannels = channels;
        Buffer.NumSamples = samples;
        Buffer.Data = (float**)NativeMemory.AllocZeroed((nuint)channels, (nuint)sizeof(float*));
        for (int c = 0; c < channels; c++)
            Buffer.Data[c] = (float*)NativeMemory.AllocZeroed((nuint)samples, sizeof(float));
    }

    public float* this[int channel] => Buffer.Data[channel];

    public void Dispose()
    {
        if (Buffer.Data == null) return;
        for (int c = 0; c < Buffer.NumChannels; c++) NativeMemory.Free(Buffer.Data[c]);
        NativeMemory.Free(Buffer.Data);
        Buffer.Data = null;
    }
}
