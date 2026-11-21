using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Lavis3DAudio.Audio;

public sealed record AudioApp(int ProcessId, string ProcessName, string DisplayName);

/// <summary>
/// Owns the playback graph: source → stereo → EQ → 3D surround (Steam Audio) → booster/limiter → spectrum tap → WASAPI.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    WasapiOut? output;
    AudioFileReader? reader;
    IWaveIn? capture;
    SurroundProcessor? surround;
    readonly SynchronizationContext? uiContext = SynchronizationContext.Current;

    public DspSettings Settings { get; } = new();
    public SpectrumTap? Tap { get; private set; }
    public BoosterProvider? Booster { get; private set; }

    public bool HasTrack => reader != null;
    public bool IsLive => capture != null;
    public WaveFormat? SourceFormat => reader?.WaveFormat;
    public PlaybackState State => output?.PlaybackState ?? PlaybackState.Stopped;
    public TimeSpan Duration => reader?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan Position
    {
        get => reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (reader != null) reader.CurrentTime = value; }
    }

    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackError;

    public static List<MMDevice> RenderDevices() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    public static List<MMDevice> CaptureDevices() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

    public static string? DefaultRenderId()
    {
        try { return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { return null; }
    }

    /// <summary>Apps that currently have an audio session on any output device.</summary>
    public static List<AudioApp> AudioApps()
    {
        var found = new List<(AudioApp App, bool Active)>();
        int self = Environment.ProcessId;
        foreach (var device in RenderDevices())
        {
            SessionCollection sessions;
            try
            {
                sessions = device.AudioSessionManager.Sessions;
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Listing audio apps on {device.FriendlyName}", ex);
                continue;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == self || session.IsSystemSoundsSession) continue;
                    using var process = Process.GetProcessById(pid);
                    string name = process.ProcessName;
                    string display = name;
                    try { display = process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name; }
                    catch { /* protected process: keep the exe name */ }
                    found.Add((new AudioApp(pid, name, display), session.State == AudioSessionState.AudioSessionStateActive));
                }
                catch (ArgumentException)
                {
                    // Process already exited.
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Reading an audio session", ex);
                }
            }
        }
        return found.OrderByDescending(f => f.Active)
            .Select(f => f.App)
            .DistinctBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void LoadFile(string path, MMDevice? device)
    {
        Stop();
        reader = new AudioFileReader(path);
        StartOutput(reader, device);
    }

    /// <summary>Processes live audio from <paramref name="source"/>; the engine takes ownership of it.</summary>
    public void StartLive(IWaveIn source, MMDevice? device)
    {
        Stop();
        capture = source;

        var cf = capture.WaveFormat;
        var format = cf.BitsPerSample == 32
            ? WaveFormat.CreateIeeeFloatWaveFormat(cf.SampleRate, cf.Channels)
            : new WaveFormat(cf.SampleRate, cf.BitsPerSample, cf.Channels);
        var buffered = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };
        capture.DataAvailable += (_, e) =>
        {
            // Capture and render clocks drift apart; drop the backlog to keep latency low.
            if (buffered.BufferedDuration > TimeSpan.FromMilliseconds(200)) buffered.ClearBuffer();
            buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception == null) return;
            if (uiContext != null) uiContext.Post(_ => PlaybackError?.Invoke(this, e.Exception), null);
            else PlaybackError?.Invoke(this, e.Exception);
        };

        StartOutput(buffered.ToSampleProvider(), device);
        capture.StartRecording();
        output!.Play();
    }

    public void Play() => output?.Play();

    public void Pause() => output?.Pause();

    public void Stop()
    {
        var oldOutput = output;
        var oldCapture = capture;
        var oldReader = reader;
        var oldSurround = surround;
        output = null;
        capture = null;
        reader = null;
        surround = null;
        Tap = null;
        Booster = null;

        // Release everything even if one device driver throws while shutting down.
        if (oldOutput != null)
        {
            oldOutput.PlaybackStopped -= OnPlaybackStopped;
            SafeRun("Stopping audio output", oldOutput.Stop);
            SafeRun("Releasing audio output", oldOutput.Dispose);
        }
        if (oldCapture != null)
        {
            SafeRun("Stopping live capture", oldCapture.StopRecording);
            SafeRun("Releasing live capture", oldCapture.Dispose);
        }
        if (oldReader != null) SafeRun("Closing audio file", oldReader.Dispose);
        if (oldSurround != null) SafeRun("Releasing 3D surround", oldSurround.Dispose);
    }

    static void SafeRun(string context, Action action)
    {
        try { action(); }
        catch (Exception ex) { ErrorLog.Write(context, ex); }
    }

    void StartOutput(ISampleProvider source, MMDevice? device)
    {
        ISampleProvider chain = new StereoAdapter(source);
        chain = new EqualizerProvider(chain, Settings);
        surround = new SurroundProcessor(chain, Settings);
        Booster = new BoosterProvider(surround, Settings);
        Tap = new SpectrumTap(Booster);

        output = device == null
            ? new WasapiOut(AudioClientShareMode.Shared, 100)
            : new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(Tap);
    }

    void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) PlaybackError?.Invoke(this, e.Exception);
        else if (reader != null) TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
