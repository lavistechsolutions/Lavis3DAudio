using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lavis3DAudio.Audio;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lavis3DAudio;

public sealed record TrackItem(string FilePath)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(FilePath);
    public string Format => System.IO.Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
}

public sealed record DeviceItem(string Id, string Name, MMDevice Device)
{
    public override string ToString() => Name;
}

public enum InputKind { Device, Loopback, App, System }

public sealed record SourceItem(string Key, string Label, InputKind Kind, MMDevice? Device = null, string? ProcessName = null)
{
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    const int BarCount = 48;
    const string CustomPreset = "Custom";
    static readonly string[] AudioExtensions = { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif" };
    static readonly string[] BandLabels = { "32", "64", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };
    static readonly Geometry PlayGeometry = Geometry.Parse("M0,0 L0,22 18,11Z");
    static readonly Geometry PauseGeometry = Geometry.Parse("M0,0 H6 V22 H0Z M11,0 H17 V22 H11Z");

    static readonly Dictionary<string, double[]> Presets = new()
    {
        ["Flat"] = new double[10],
        ["Bass Boost"] = new double[] { 7, 6, 5, 3, 1, 0, 0, 0, 0, 0 },
        ["Treble Boost"] = new double[] { 0, 0, 0, 0, 0, 1, 3, 5, 6, 7 },
        ["Vocal"] = new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
        ["Rock"] = new double[] { 5, 4, 2, -1, -2, -1, 2, 4, 5, 5 },
        ["Pop"] = new double[] { -1, 1, 3, 4, 3, 0, -1, -1, 1, 2 },
        ["Jazz"] = new double[] { 3, 2, 1, 2, -1, -1, 0, 1, 2, 3 },
        ["Classical"] = new double[] { 4, 3, 2, 1, -1, -1, 0, 2, 3, 4 },
        ["Electronic"] = new double[] { 6, 5, 1, 0, -2, 2, 1, 2, 5, 6 },
        ["Movie"] = new double[] { 5, 4, 2, 0, 1, 3, 3, 2, 3, 2 },
    };

    readonly AppSettings settings;
    readonly AudioEngine engine = new();
    readonly ObservableCollection<TrackItem> playlist = new();
    readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly Rectangle[] bars = new Rectangle[BarCount];
    readonly float[] bandValues = new float[BarCount];
    readonly float[] barLevels = new float[BarCount];
    readonly Slider[] eqSliders = new Slider[AppSettings.EqBandCount];
    readonly TextBlock[] eqValues = new TextBlock[AppSettings.EqBandCount];
    List<DeviceItem> outputs = new();
    List<SourceItem> inputs = new();

    int currentIndex = -1, resumeIndex = -1;
    int trackPlays, playlistPasses;
    bool seekDragging, updatingSeek, syncingEq, applyingPreset, refreshingSources, uiTickFailed;
    float meterL, meterR;

    public MainWindow()
    {
        settings = AppSettings.Load();
        if (settings.StartIn != StartupSource.LastUsed)
            settings.SourceMode = settings.StartIn == StartupSource.Live ? SourceMode.Live : SourceMode.Player;

        InitializeComponent();
        RestoreWindowBounds();

        string? resumePath = settings.CurrentIndex >= 0 && settings.CurrentIndex < settings.Playlist.Count
            ? settings.Playlist[settings.CurrentIndex]
            : null;
        foreach (var path in settings.Playlist.Where(System.IO.File.Exists))
            playlist.Add(new TrackItem(path));
        resumeIndex = playlist.ToList().FindIndex(t => t.FilePath == resumePath);
        Playlist.ItemsSource = playlist;

        BuildSpectrum();
        BuildEqualizer();
        LoadOutputDevices();
        RestoreWindowsOutput();
        RefreshInputSources();
        DataContext = settings;
        SyncEqSliders();
        WireControls();

        playlist.CollectionChanged += (_, _) =>
        {
            settings.Playlist = playlist.Select(t => t.FilePath).ToList();
            ScheduleSave();
        };
        settings.PropertyChanged += OnSettingChanged;
        engine.TrackEnded += (_, _) => OnTrackEnded();
        engine.PlaybackError += (_, ex) =>
        {
            ShowError(ex.Message);
            if (engine.IsLive) StopLive();
            else RestoreWindowsOutput();
            UpdatePlayIcon();
        };

        ApplyToEngine();
        UpdateLoopBadge();
        ShowSource(settings.SourceMode == SourceMode.Live);

        ErrorLog.Reported += OnErrorReported;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        uiTimer.Tick += OnUiTick;
        uiTimer.Start();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ErrorLog.Reported -= OnErrorReported;
            uiTimer.Stop();
            engine.Dispose();
            RestoreWindowsOutput();
        };
    }

    // ---------- Startup / shutdown ----------

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RestoreSession();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Restoring last session", ex);
        }
    }

    void RestoreSession()
    {
        var files = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (files.Count > 0)
        {
            settings.SourceMode = SourceMode.Player;
            AddFiles(files);
            return;
        }

        if (settings.SourceMode == SourceMode.Player && settings.ResumeLastTrack && resumeIndex >= 0)
        {
            OpenTrack(resumeIndex, autoplay: false, TimeSpan.FromSeconds(settings.ResumePosition));
        }
        else if (settings.SourceMode == SourceMode.Live && settings.AutoStartLive)
        {
            StartLive();
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            settings.CurrentIndex = currentIndex;
            settings.ResumePosition = engine.HasTrack ? engine.Position.TotalSeconds : 0;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Collecting window state on close", ex);
        }
        saveTimer.Stop();
        SaveSettings();
    }

    void RestoreWindowBounds()
    {
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowLeft is double left && settings.WindowTop is double top)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(left, top, 200, 100)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Audio effects switch themselves off on error; tell the user without interrupting playback.</summary>
    void OnErrorReported(string context, Exception ex)
    {
        if (!context.EndsWith("after an error", StringComparison.Ordinal)) return;
        Dispatcher.BeginInvoke(() => TrackInfo.Text = $"⚠ {context}. Restart playback to retry. Details: {ErrorLog.FilePath}");
    }

    void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveSettings()
    {
        settings.CurrentIndex = currentIndex;
        try { settings.Save(); }
        catch (Exception ex) { ErrorLog.Write("Saving settings", ex); }
    }

    // ---------- Setup ----------

    void BuildSpectrum()
    {
        var fill = new LinearGradientBrush(
            Color.FromRgb(0x00, 0xE5, 0xFF), Color.FromRgb(0xB0, 0x4B, 0xFF), new Point(0, 1), new Point(0, 0));
        fill.Freeze();

        for (int i = 0; i < BarCount; i++)
        {
            bars[i] = new Rectangle { Fill = fill, RadiusX = 2, RadiusY = 2, Height = 2 };
            Spectrum.Children.Add(bars[i]);
        }
        Spectrum.SizeChanged += (_, _) =>
        {
            double slot = Spectrum.ActualWidth / BarCount;
            for (int i = 0; i < BarCount; i++)
            {
                bars[i].Width = Math.Max(1, slot * 0.7);
                Canvas.SetLeft(bars[i], i * slot);
            }
        };
    }

    void BuildEqualizer()
    {
        var sliderStyle = (Style)FindResource("NeonSliderV");
        var mutedStyle = (Style)FindResource("Muted");

        for (int i = 0; i < eqSliders.Length; i++)
        {
            int band = i;
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            eqValues[i] = new TextBlock { Text = "0", Style = mutedStyle, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            eqSliders[i] = new Slider
            {
                Style = sliderStyle,
                Minimum = -12,
                Maximum = 12,
                SmallChange = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            var label = new TextBlock { Text = BandLabels[i], FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };

            Grid.SetRow(eqSliders[i], 1);
            Grid.SetRow(label, 2);
            grid.Children.Add(eqValues[i]);
            grid.Children.Add(eqSliders[i]);
            grid.Children.Add(label);
            EqBands.Children.Add(grid);

            eqSliders[i].ValueChanged += (_, e) => OnEqSliderChanged(band, e.NewValue);
        }

        EqPreset.ItemsSource = Presets.Keys.Append(CustomPreset).ToList();
    }

    void LoadOutputDevices()
    {
        try
        {
            outputs = AudioEngine.RenderDevices().Select(d => new DeviceItem(d.ID, d.FriendlyName, d)).ToList();
        }
        catch (Exception ex)
        {
            ShowError("Could not list audio devices: " + ex.Message);
        }
        OutputDevice.ItemsSource = outputs;
        SettingsOutput.ItemsSource = outputs;
        if (outputs.All(o => o.Id != settings.OutputDeviceId))
            settings.OutputDeviceId = AudioEngine.DefaultRenderId() ?? outputs.FirstOrDefault()?.Id;
    }

    void RefreshInputSources()
    {
        var list = new List<SourceItem>();
        try
        {
            if (DefaultDevice.FindVirtual() is { } virtualDevice)
                list.Add(new SourceItem("system", "All PC sound (system-wide)", InputKind.System, virtualDevice));
            if (ProcessLoopbackCapture.IsSupported)
            {
                foreach (var app in AudioEngine.AudioApps())
                    list.Add(new SourceItem("app:" + app.ProcessName.ToLowerInvariant(), "App: " + app.DisplayName, InputKind.App, ProcessName: app.ProcessName));
            }
            foreach (var d in AudioEngine.CaptureDevices())
                list.Add(new SourceItem("dev:" + d.ID, "Input: " + d.FriendlyName, InputKind.Device, d));
            foreach (var o in outputs)
                list.Add(new SourceItem("loop:" + o.Id, "System audio: " + o.Name, InputKind.Loopback, o.Device));
        }
        catch (Exception ex)
        {
            ShowError("Could not list input sources: " + ex.Message);
        }

        string? key = settings.InputSourceKey;
        if (key != null && key.StartsWith("app:") && list.All(s => s.Key != key))
        {
            string name = key["app:".Length..];
            list.Insert(0, new SourceItem(key, $"App: {name} (not playing yet)", InputKind.App, ProcessName: name));
        }
        if (key == null || list.All(s => s.Key != key))
        {
            key = (list.FirstOrDefault(s => s.Kind == InputKind.System)
                   ?? list.FirstOrDefault(s => s.Kind == InputKind.Device && s.Label.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(s => s.Kind == InputKind.App)
                   ?? list.FirstOrDefault())?.Key;
        }

        refreshingSources = true;
        inputs = list;
        LiveInput.ItemsSource = list;
        SettingsInput.ItemsSource = list;
        settings.InputSourceKey = null;
        settings.InputSourceKey = key;
        refreshingSources = false;
    }

    void WireControls()
    {
        Seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => seekDragging = true));
        Seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            seekDragging = false;
            engine.Position = TimeSpan.FromSeconds(Seek.Value);
        }));
        Seek.ValueChanged += (_, e) =>
        {
            if (!updatingSeek && !seekDragging && engine.HasTrack) engine.Position = TimeSpan.FromSeconds(e.NewValue);
        };

        PlayButton.Click += (_, _) => TogglePlay();
        StopButton.Click += (_, _) => StopPlayback();
        PrevButton.Click += (_, _) =>
        {
            if (engine.HasTrack && engine.Position.TotalSeconds > 3) engine.Position = TimeSpan.Zero;
            else if (currentIndex > 0) PlayTrack(currentIndex - 1);
        };
        NextButton.Click += (_, _) => { if (currentIndex + 1 < playlist.Count) PlayTrack(currentIndex + 1); };
        ThumbPrev.Click += (_, _) => PrevButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ThumbPlay.Click += (_, _) => PlayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ThumbNext.Click += (_, _) => NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        LoopButton.Click += (_, _) => LoopPopup.IsOpen = true;
        OpenButton.Click += (_, _) => OpenFiles();
        AddButton.Click += (_, _) => OpenFiles();
        ClearButton.Click += (_, _) =>
        {
            StopPlayback();
            playlist.Clear();
            currentIndex = -1;
            TrackTitle.Text = "Drop music here or click Open";
            TrackInfo.Text = "";
        };
        Playlist.MouseDoubleClick += (_, _) => { if (Playlist.SelectedIndex >= 0) PlayTrack(Playlist.SelectedIndex); };
        Playlist.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || Playlist.SelectedIndex < 0) return;
            int index = Playlist.SelectedIndex;
            if (index == currentIndex) { StopPlayback(); currentIndex = -1; }
            else if (index < currentIndex) currentIndex--;
            playlist.RemoveAt(index);
        };

        LiveButton.Click += (_, _) => { if (engine.IsLive) StopLive(); else StartLive(); };
        RefreshSourcesButton.Click += (_, _) => RefreshInputSources();
        SettingsRefreshButton.Click += (_, _) => RefreshInputSources();
        OpenMixerButton.Click += (_, _) => OpenShell("ms-settings:apps-volume");
        EqReset.Click += (_, _) => settings.EqPreset = "Flat";

        SettingsButton.Click += (_, _) => OpenSettings();
        CloseSettingsButton.Click += (_, _) => CloseSettings();
        AboutButton.Click += (_, _) => OpenAbout();
        SettingsOverlay.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == SettingsOverlay) CloseSettings(); };
        ResetSoundButton.Click += (_, _) => settings.ResetSound();
        ResetAllButton.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset all settings to their defaults? Your playlist is kept.", "Lavis 3D Audio",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                settings.ResetAll();
        };
        OpenSettingsFolderButton.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            OpenShell(AppSettings.Folder);
        };

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) => UpdateMaximizedLayout();
        SourceInitialized += (_, _) => UpdateMaximizedLayout();

        Drop += OnDrop;
        PreviewKeyDown += (_, e) =>
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
        };
    }

    void UpdateMaximizedLayout()
    {
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = new Thickness(max ? 7 : 0);
        MaxButton.Content = max ? "\uE923" : "\uE922";
    }

    void OpenSettings()
    {
        RefreshInputSources();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    void OpenAbout()
    {
        var existing = System.Windows.Application.Current.Windows.OfType<AboutWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        new AboutWindow { Owner = this }.Show();
    }

    // ---------- Settings ----------

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.OutputDeviceId):
                OnOutputChanged();
                break;
            case nameof(AppSettings.InputSourceKey):
                if (!refreshingSources && engine.IsLive) StartLive();
                break;
            case nameof(AppSettings.SourceMode):
                SwitchSource(settings.SourceMode == SourceMode.Live);
                break;
            case nameof(AppSettings.EqPreset):
                ApplyPreset();
                break;
            case nameof(AppSettings.EqGains):
                SyncEqSliders();
                break;
            case nameof(AppSettings.LoopMode):
            case nameof(AppSettings.LoopTimes):
            case nameof(AppSettings.LoopScope):
                UpdateLoopBadge();
                break;
        }
        ApplyToEngine();
        ScheduleSave();
    }

    void ApplyToEngine()
    {
        var s = engine.Settings;
        s.SurroundEnabled = settings.SurroundEnabled;
        s.HeadphoneMode = settings.HeadphoneMode;
        s.Intensity = (float)(settings.Intensity / 100);
        s.Ambience = (float)(settings.Ambience / 100);
        s.OrbitSpeed = settings.Orbit < 0.02 ? 0 : (float)settings.Orbit;
        double db = 20 * Math.Log10(Math.Max(100, settings.Boost) / 100);
        s.BoostDb = (float)db;
        s.Volume = (float)(settings.Volume / 100);
        s.EqEnabled = settings.EqEnabled;

        bool eqChanged = false;
        for (int i = 0; i < s.EqGains.Length; i++)
        {
            float gain = (float)settings.EqGains[i];
            if (s.EqGains[i] == gain) continue;
            s.EqGains[i] = gain;
            eqChanged = true;
        }
        if (eqChanged) Interlocked.Increment(ref s.EqVersion);

        OrbitValue.Text = s.OrbitSpeed == 0 ? "Off" : $"{settings.Orbit:0.00} rev/s";
        BoostDb.Text = $"+{db:0.0} dB gain";
    }

    void OnEqSliderChanged(int band, double value)
    {
        eqValues[band].Text = value > 0 ? $"+{value:0.#}" : $"{value:0.#}";
        if (syncingEq) return;

        var gains = (double[])settings.EqGains.Clone();
        gains[band] = value;
        settings.EqGains = gains;
        if (settings.EqPreset != CustomPreset)
        {
            applyingPreset = true;
            settings.EqPreset = CustomPreset;
            applyingPreset = false;
        }
    }

    void SyncEqSliders()
    {
        syncingEq = true;
        for (int i = 0; i < eqSliders.Length; i++)
        {
            eqSliders[i].Value = settings.EqGains[i];
            eqValues[i].Text = settings.EqGains[i] > 0 ? $"+{settings.EqGains[i]:0.#}" : $"{settings.EqGains[i]:0.#}";
        }
        syncingEq = false;
    }

    void ApplyPreset()
    {
        if (applyingPreset || !Presets.TryGetValue(settings.EqPreset, out var gains)) return;
        settings.EqGains = (double[])gains.Clone();
    }

    void LoopLess_Click(object sender, RoutedEventArgs e) => settings.LoopTimes--;

    void LoopMore_Click(object sender, RoutedEventArgs e) => settings.LoopTimes++;

    void UpdateLoopBadge()
    {
        string what = settings.LoopScope == LoopScope.Track ? "current track" : "playlist";
        (LoopBadge.Text, LoopButton.ToolTip) = settings.LoopMode switch
        {
            LoopMode.Endless => ("∞", $"Loop: repeat {what} endlessly"),
            LoopMode.Times => ($"×{settings.LoopTimes}", $"Loop: play {what} {settings.LoopTimes} times"),
            _ => ("", "Loop: off (play once)"),
        };
        LoopIcon.Stroke = settings.LoopMode == LoopMode.Once
            ? (Brush)FindResource("MutedBrush")
            : (Brush)FindResource("AccentSolidBrush");
    }

    // ---------- Playback ----------

    MMDevice? SelectedOutput() => outputs.FirstOrDefault(o => o.Id == settings.OutputDeviceId)?.Device;

    void OpenFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio files|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    void AddFiles(IEnumerable<string> paths)
    {
        int firstNew = playlist.Count;
        foreach (var path in paths.Where(p => AudioExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())))
            playlist.Add(new TrackItem(path));

        if (!engine.HasTrack && !engine.IsLive && playlist.Count > firstNew) PlayTrack(firstNew);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items) return;
        var files = items.SelectMany(p => System.IO.Directory.Exists(p)
            ? System.IO.Directory.EnumerateFiles(p, "*", System.IO.SearchOption.AllDirectories)
            : new[] { p });
        settings.SourceMode = SourceMode.Player;
        AddFiles(files.ToList());
    }

    /// <summary>User-initiated play: restarts the loop counters.</summary>
    void PlayTrack(int index)
    {
        trackPlays = 0;
        playlistPasses = 0;
        OpenTrack(index, autoplay: true);
    }

    void OpenTrack(int index, bool autoplay, TimeSpan? position = null)
    {
        if (index < 0 || index >= playlist.Count)
        {
            StopPlayback();
            return;
        }

        var track = playlist[index];
        try
        {
            engine.LoadFile(track.FilePath, SelectedOutput());
            if (position is { } p && p < engine.Duration) engine.Position = p;
            if (autoplay) engine.Play();
            currentIndex = index;
            Playlist.SelectedIndex = index;
            Playlist.ScrollIntoView(track);
            TrackTitle.Text = track.Name;
            var fmt = engine.SourceFormat!;
            TrackInfo.Text = $"{track.Format}  •  {fmt.SampleRate / 1000.0:0.#} kHz  •  {Format(engine.Duration)}";
            ScheduleSave();
        }
        catch (Exception ex)
        {
            ShowError($"Could not play \"{track.Name}\".\n{ex.Message}");
        }
        UpdatePlayIcon();
    }

    void OnTrackEnded()
    {
        trackPlays++;
        if (settings.LoopScope == LoopScope.Track && ShouldRepeat(trackPlays))
        {
            OpenTrack(currentIndex, autoplay: true);
            return;
        }

        trackPlays = 0;
        if (currentIndex + 1 < playlist.Count)
        {
            OpenTrack(currentIndex + 1, autoplay: true);
            return;
        }

        playlistPasses++;
        if (settings.LoopScope == LoopScope.Playlist && playlist.Count > 0 && ShouldRepeat(playlistPasses))
        {
            OpenTrack(0, autoplay: true);
            return;
        }

        playlistPasses = 0;
        StopPlayback();
    }

    bool ShouldRepeat(int completedPlays) => settings.LoopMode switch
    {
        LoopMode.Endless => true,
        LoopMode.Times => completedPlays < settings.LoopTimes,
        _ => false,
    };

    void StopPlayback()
    {
        engine.Stop();
        UpdatePlayIcon();
        ResetSeek();
    }

    void TogglePlay()
    {
        if (settings.SourceMode == SourceMode.Live) return;
        if (!engine.HasTrack)
        {
            if (playlist.Count == 0) OpenFiles();
            else PlayTrack(Math.Max(0, Playlist.SelectedIndex));
            return;
        }
        if (engine.State == PlaybackState.Playing) engine.Pause();
        else engine.Play();
        UpdatePlayIcon();
    }

    void OnOutputChanged()
    {
        if (engine.IsLive)
        {
            StartLive();
        }
        else if (engine.HasTrack)
        {
            var position = engine.Position;
            bool playing = engine.State == PlaybackState.Playing;
            OpenTrack(currentIndex, playing, position);
        }
    }

    void SwitchSource(bool live)
    {
        engine.Stop();
        RestoreWindowsOutput();
        UpdatePlayIcon();
        ResetSeek();
        if (live) RefreshInputSources();
        ShowSource(live);
    }

    void ShowSource(bool live)
    {
        PlayerPanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        LivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Transport.IsEnabled = !live;
        Seek.IsEnabled = !live;
        LiveButton.Content = "Start";
        TrackTitle.Text = live ? "Live input" : currentIndex >= 0 ? playlist[currentIndex].Name : "Drop music here or click Open";
        TrackInfo.Text = live ? "Pick a source and press Start" : "";
    }

    // ---------- Live input ----------

    void StartLive()
    {
        engine.Stop();
        var source = inputs.FirstOrDefault(i => i.Key == settings.InputSourceKey);
        if (source == null)
        {
            ShowError("Select an input source first.");
            return;
        }
        if (source.Kind != InputKind.System) RestoreWindowsOutput();
        var output = SelectedOutput();

        try
        {
            IWaveIn capture;
            switch (source.Kind)
            {
                case InputKind.System:
                    if (!PrepareSystemWide(source.Device!, ref output)) return;
                    capture = new WasapiLoopbackCapture(source.Device);
                    break;
                case InputKind.App:
                    var app = AudioEngine.AudioApps().FirstOrDefault(a =>
                        a.ProcessName.Equals(source.ProcessName, StringComparison.OrdinalIgnoreCase));
                    if (app == null)
                    {
                        ShowError($"{source.Label.Replace("App: ", "")} isn't playing audio right now. Start playback in it, press Refresh, then Start.");
                        return;
                    }
                    capture = new ProcessLoopbackCapture(app.ProcessId);
                    break;
                case InputKind.Loopback:
                    string outputId = output?.ID ?? AudioEngine.DefaultRenderId() ?? "";
                    if (source.Device!.ID == outputId)
                    {
                        ShowError("Capturing the same device you are playing to would create a feedback loop. Pick a different output device.");
                        return;
                    }
                    capture = new WasapiLoopbackCapture(source.Device);
                    break;
                default:
                    capture = new WasapiCapture(source.Device!, true, 20);
                    break;
            }

            engine.StartLive(capture, output);
            LiveButton.Content = "Stop";
            TrackTitle.Text = "Live: " + source.Label;
            TrackInfo.Text = "Processing in real time → " + (output?.FriendlyName ?? "default output");
        }
        catch (Exception ex)
        {
            engine.Stop();
            RestoreWindowsOutput();
            ShowError("Could not start live processing: " + ex.Message);
        }
    }

    void StopLive()
    {
        engine.Stop();
        RestoreWindowsOutput();
        LiveButton.Content = "Start";
        TrackInfo.Text = "Stopped";
    }

    /// <summary>
    /// Routes every app through the FxSound virtual device: it becomes the Windows default, we capture it and
    /// play the processed result to real speakers. The previous default is remembered for <see cref="RestoreWindowsOutput"/>.
    /// </summary>
    bool PrepareSystemWide(MMDevice virtualDevice, ref MMDevice? output)
    {
        var fxSound = Process.GetProcessesByName("FxSound");
        if (fxSound.Length > 0)
        {
            var answer = MessageBox.Show(this,
                "FxSound is running and already uses the FxSound Speakers device, so you would hear everything twice.\n\nClose FxSound now?",
                "Lavis 3D Audio", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
            foreach (var p in fxSound)
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception ex) { ErrorLog.Write("Closing FxSound", ex); }
                finally { p.Dispose(); }
            }
        }

        var currentDefault = outputs.FirstOrDefault(o => o.Id == AudioEngine.DefaultRenderId())?.Device;
        if (output == null || DefaultDevice.IsVirtual(output))
            output = currentDefault != null && !DefaultDevice.IsVirtual(currentDefault) ? currentDefault : null;
        if (output == null)
        {
            ShowError("Choose your speakers or headphones under \"Output device\" first.");
            return false;
        }

        if (settings.RestoreDefaultDeviceId == null)
        {
            settings.RestoreDefaultDeviceId = currentDefault != null && !DefaultDevice.IsVirtual(currentDefault)
                ? currentDefault.ID
                : output.ID;
            SaveSettings();
        }
        DefaultDevice.Set(virtualDevice.ID);
        return true;
    }

    /// <summary>Puts back the Windows default output that system-wide mode replaced, if any.</summary>
    void RestoreWindowsOutput()
    {
        string? id = settings.RestoreDefaultDeviceId;
        if (id == null) return;
        try
        {
            if (outputs.Any(o => o.Id == id)) DefaultDevice.Set(id);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Restoring the Windows default output", ex);
        }
        settings.RestoreDefaultDeviceId = null;
        SaveSettings();
    }

    // ---------- UI refresh ----------

    void OnUiTick(object? sender, EventArgs e)
    {
        // Runs 30x per second: log a failure once instead of flooding the user with dialogs.
        try
        {
            RefreshMeters();
        }
        catch (Exception ex)
        {
            if (!uiTickFailed) ErrorLog.Write("Refreshing visualizer", ex);
            uiTickFailed = true;
        }
    }

    void RefreshMeters()
    {
        bool playing = engine.State == PlaybackState.Playing;
        var tap = engine.Tap;
        if (playing && tap != null) tap.GetBands(bandValues);

        double h = Spectrum.ActualHeight;
        for (int i = 0; i < BarCount; i++)
        {
            float target = playing ? bandValues[i] : 0;
            barLevels[i] = target > barLevels[i] ? target : Math.Max(0, barLevels[i] - 0.03f);
            double barHeight = Math.Max(3, barLevels[i] * h);
            bars[i].Height = barHeight;
            Canvas.SetTop(bars[i], h - barHeight);
        }

        var (pl, pr) = playing && engine.Booster != null ? engine.Booster.TakePeaks() : (0f, 0f);
        meterL = Math.Max(ToMeter(pl), meterL - 0.04f);
        meterR = Math.Max(ToMeter(pr), meterR - 0.04f);
        MeterLScale.ScaleY = meterL;
        MeterRScale.ScaleY = meterR;
        LimiterDot.Opacity = playing && engine.Booster?.Limiting == true ? 1 : 0.25;

        if (engine.HasTrack && !seekDragging)
        {
            updatingSeek = true;
            Seek.Maximum = Math.Max(1, engine.Duration.TotalSeconds);
            Seek.Value = engine.Position.TotalSeconds;
            updatingSeek = false;
            TimeText.Text = Format(engine.Position);
            DurationText.Text = Format(engine.Duration);
        }
    }

    static float ToMeter(float peak) => (float)Math.Clamp((20 * Math.Log10(peak + 1e-6) + 48) / 48, 0, 1);

    void ResetSeek()
    {
        updatingSeek = true;
        Seek.Value = 0;
        updatingSeek = false;
        TimeText.Text = DurationText.Text = "0:00";
    }

    void UpdatePlayIcon()
    {
        bool playing = engine.State == PlaybackState.Playing;
        PlayIcon.Data = playing ? PauseGeometry : PlayGeometry;
        PlayIcon.Margin = new Thickness(playing ? 0 : 4, 0, 0, 0);
        ThumbPlay.ImageSource = (ImageSource)FindResource(playing ? "ThumbPauseImage" : "ThumbPlayImage");
        ThumbPlay.Description = playing ? "Pause" : "Play";
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* nothing sensible to do if the shell refuses */ }
    }

    void ShowError(string message) => MessageBox.Show(this, message, "Lavis 3D Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
}
