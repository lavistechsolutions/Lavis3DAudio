using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Lavis3DAudio.Audio;

/// <summary>
/// Captures the audio of a single process (and its child processes) using WASAPI process loopback.
/// Requires Windows 10 version 2004 (build 19041) or later.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    const string VirtualDevice = "VAD\\Process_Loopback";
    const uint StreamFlagsLoopback = 0x00020000;
    const uint StreamFlagsEventCallback = 0x00040000;
    const uint StreamFlagsAutoConvertPcm = 0x80000000;
    const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    const uint BufferFlagsSilent = 0x2;

    static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    readonly int processId;
    readonly bool includeTree;
    IAudioClient? client;
    IAudioCaptureClient? captureClient;
    Thread? thread;
    volatile bool stopping;

    public ProcessLoopbackCapture(int processId, bool includeProcessTree = true)
    {
        this.processId = processId;
        includeTree = includeProcessTree;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    public void StartRecording()
    {
        if (thread != null) return;
        // Activation blocks on a COM callback, so keep it off the (STA) UI thread.
        Task.Run(Initialize).GetAwaiter().GetResult();

        stopping = false;
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopbackCapture", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Initialize()
    {
        client = Activate(processId, includeTree);

        var format = new WaveFormatEx
        {
            FormatTag = 3,
            Channels = (short)WaveFormat.Channels,
            SamplesPerSec = WaveFormat.SampleRate,
            BitsPerSample = 32,
            BlockAlign = (short)(WaveFormat.Channels * 4),
            AvgBytesPerSec = WaveFormat.SampleRate * WaveFormat.Channels * 4,
        };
        IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, pFormat, false);
            Check(client.Initialize(0,
                StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                200_000, 0, pFormat, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(pFormat);
        }

        var iid = IidAudioCaptureClient;
        Check(client.GetService(ref iid, out object service));
        captureClient = (IAudioCaptureClient)service;
    }

    public void StopRecording()
    {
        stopping = true;
        thread?.Join(1000);
        thread = null;
    }

    void CaptureLoop()
    {
        Exception? error = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            Check(client!.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Check(client.Start());
            int blockAlign = WaveFormat.BlockAlign;
            byte[] buffer = new byte[WaveFormat.AverageBytesPerSecond];

            while (!stopping)
            {
                ready.WaitOne(100);
                while (!stopping)
                {
                    Check(captureClient!.GetNextPacketSize(out uint packet));
                    if (packet == 0) break;
                    Check(captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & BufferFlagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    Check(captureClient.ReleaseBuffer(frames));
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            error = ex;
            ErrorLog.Write("App audio capture stopped", ex);
        }

        try { RecordingStopped?.Invoke(this, new StoppedEventArgs(error)); }
        catch (Exception ex) { ErrorLog.Write("RecordingStopped handler", ex); }
    }

    static IAudioClient Activate(int processId, bool includeTree)
    {
        var activation = new ActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            LoopbackMode = includeTree ? 0 : 1,
        };
        int size = Marshal.SizeOf<ActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(size);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(activation, pParams, false);
            Marshal.StructureToPtr(new BlobPropVariant { VarType = 65 /* VT_BLOB */, BlobSize = size, BlobData = pParams }, pVariant, false);

            var handler = new CompletionHandler();
            var iid = IidAudioClient;
            ActivateAudioInterfaceAsync(VirtualDevice, ref iid, pVariant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out activating process audio capture.");
            Check(handler.Result);
            return (IAudioClient)handler.Interface!;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        try
        {
            StopRecording();
            if (captureClient != null) Marshal.ReleaseComObject(captureClient);
            if (client != null) Marshal.ReleaseComObject(client);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Releasing app audio capture", ex);
        }
        finally
        {
            captureClient = null;
            client = null;
        }
    }

    // ---------- Interop ----------

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BlobPropVariant
    {
        public ushort VarType;
        public ushort Reserved1, Reserved2, Reserved3;
        public int BlobSize;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result;
        public object? Interface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            operation.GetActivateResult(out Result, out Interface);
            Done.Set();
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
