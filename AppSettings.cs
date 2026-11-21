using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lavis3DAudio;

public enum SourceMode { Player, Live }
public enum StartupSource { LastUsed, Player, Live }
public enum LoopMode { Once, Times, Endless }
public enum LoopScope { Track, Playlist }

/// <summary>Everything the user can change, persisted to %AppData%\Lavis3DAudio\settings.json.</summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public const int EqBandCount = 10;
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lavis3DAudio");
    public static readonly string FilePath = Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    bool surroundEnabled = true, headphoneMode = true, eqEnabled = true;
    double intensity = 70, ambience = 25, orbit, boost = 100, volume = 80;
    string eqPreset = "Flat";
    double[] eqGains = new double[EqBandCount];
    string? outputDeviceId, inputSourceKey;
    SourceMode sourceMode;
    LoopMode loopMode;
    int loopTimes = 2;
    LoopScope loopScope = LoopScope.Playlist;
    bool rememberSound = true, savePlaylist = true, resumeLastTrack = true, autoStartLive;
    StartupSource startIn;

    public event PropertyChangedEventHandler? PropertyChanged;

    // 3D surround
    public bool SurroundEnabled { get => surroundEnabled; set => Set(ref surroundEnabled, value); }
    public bool HeadphoneMode
    {
        get => headphoneMode;
        set { if (Set(ref headphoneMode, value)) Raise(nameof(SpeakerMode)); }
    }
    [JsonIgnore] public bool SpeakerMode { get => !HeadphoneMode; set => HeadphoneMode = !value; }
    public double Intensity { get => intensity; set => Set(ref intensity, value); }
    public double Ambience { get => ambience; set => Set(ref ambience, value); }
    public double Orbit { get => orbit; set => Set(ref orbit, value); }

    // Volume booster
    public double Boost { get => boost; set => Set(ref boost, value); }
    public double Volume { get => volume; set => Set(ref volume, value); }

    // Equalizer
    public bool EqEnabled { get => eqEnabled; set => Set(ref eqEnabled, value); }
    public string EqPreset { get => eqPreset; set => Set(ref eqPreset, value); }
    public double[] EqGains
    {
        get => eqGains;
        set => Set(ref eqGains, value is { Length: EqBandCount } ? value : new double[EqBandCount]);
    }

    // Devices & source
    public string? OutputDeviceId { get => outputDeviceId; set => Set(ref outputDeviceId, value); }
    public string? InputSourceKey { get => inputSourceKey; set => Set(ref inputSourceKey, value); }
    public SourceMode SourceMode { get => sourceMode; set => Set(ref sourceMode, value); }
    /// <summary>Windows default output to put back after system-wide mode; kept on disk in case the app crashes.</summary>
    public string? RestoreDefaultDeviceId { get; set; }

    // Playback
    public LoopMode LoopMode { get => loopMode; set => Set(ref loopMode, value); }
    public int LoopTimes { get => loopTimes; set => Set(ref loopTimes, Math.Clamp(value, 2, 99)); }
    public LoopScope LoopScope { get => loopScope; set => Set(ref loopScope, value); }
    public List<string> Playlist { get; set; } = new();
    public int CurrentIndex { get; set; } = -1;
    public double ResumePosition { get; set; }

    // Startup & saving
    public bool RememberSoundSettings { get => rememberSound; set => Set(ref rememberSound, value); }
    public bool SavePlaylist { get => savePlaylist; set => Set(ref savePlaylist, value); }
    public bool ResumeLastTrack { get => resumeLastTrack; set => Set(ref resumeLastTrack, value); }
    public bool AutoStartLive { get => autoStartLive; set => Set(ref autoStartLive, value); }
    public StartupSource StartIn { get => startIn; set => Set(ref startIn, value); }

    // Window
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    public void ResetSound()
    {
        SurroundEnabled = true;
        HeadphoneMode = true;
        Intensity = 70;
        Ambience = 25;
        Orbit = 0;
        Boost = 100;
        Volume = 80;
        EqEnabled = true;
        EqGains = new double[EqBandCount];
        EqPreset = "Flat";
    }

    public void ResetAll()
    {
        ResetSound();
        LoopMode = LoopMode.Once;
        LoopTimes = 2;
        LoopScope = LoopScope.Playlist;
        RememberSoundSettings = true;
        SavePlaylist = true;
        ResumeLastTrack = true;
        AutoStartLive = false;
        StartIn = StartupSource.LastUsed;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file falls back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        if (!RememberSoundSettings) copy.ResetSound();
        if (!SavePlaylist)
        {
            copy.Playlist = new List<string>();
            copy.CurrentIndex = -1;
            copy.ResumePosition = 0;
        }

        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(temp, FilePath, true);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
