using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Castor.AI;
using Castor.AI.Contracts.V1;
using Castor.AI.Providers;
using Castor.AI.Providers.Local;
using Castor.Proto.App.Audio;
using Castor.Proto.App.Charts;
using Castor.Proto.App.Profiles;
using Castor.Proto.App.Video;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Castor.Proto.App.ViewModels;

/// <summary>
/// Debug console for the AI team: runs one session of any worker domain described by a
/// <see cref="SessionProfile"/> and shows, live, what the real app would receive.
/// Nothing here is domain-specific: cameras and buttons come from the profile, metrics and
/// flags from the worker capabilities.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, ICameraActions
{
    private const int MaxListItems = 300;
    private const string NoCamera = "(no camera)";

    private static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#9E9E9E"));

    private readonly AiClient _ai;
    private readonly Dictionary<int, FfmpegAudioSource> _audioSources = [];
    private readonly Dictionary<string, IDisposable> _videoSources = [];
    private readonly DispatcherTimer _uiTimer;
    private readonly LatencyWindow _latency = new(30);
    private readonly List<(ChartModel Chart, MetricDescriptor Metric)> _metricCharts = [];
    private int _accepted;
    private int _rejected;
    private int _suggested;

    public MainViewModel()
    {
        // Everything AI goes through the Castor.AI SDK, exactly like Castor Studio will.
        _ai = new AiClient(new LocalWorkerProvider(), new AiClientOptions { Dispatch = Ui });
        _ai.StateChanged += (_, change) =>
        {
            ProviderState = change.State.ToString();
            StateDetail = change.Detail;
        };
        _ai.SessionStarted += (_, capabilities) => OnSessionStarted(capabilities);
        _ai.SuggestionReceived += (_, suggestion) => OnSuggestion(suggestion);
        _ai.OnAirChanged += (_, change) => OnAirChanged(change);
        _ai.StatsReceived += (_, stats) => OnStats(stats);
        _ai.LogReceived += (_, entry) => AddLog(entry.Source, entry.Message);

        LatencyChart = new ChartModel("Latency", "ms", min: 0);
        LatencyChart.GetOrAdd("decision age", Color.Parse("#FFCA28"));
        LatencyChart.GetOrAdd("worker processing", Color.Parse("#4FC3F7"));
        LatencyChart.GetOrAdd("inference avg", Color.Parse("#BA68C8"));

        foreach (var profile in ProfileStore.LoadAll(line => AddLog("app", line)))
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => OnUiTick());
    }

    public ObservableCollection<SessionProfile> Profiles { get; } = [];

    public ObservableCollection<CameraViewModel> Cameras { get; } = [];

    public ObservableCollection<DecisionItem> Decisions { get; } = [];

    public ObservableCollection<OnAirItem> OnAirHistory { get; } = [];

    public ObservableCollection<LogItem> Logs { get; } = [];

    public TimelineModel Timeline { get; } = new();

    public ChartModel LatencyChart { get; }

    /// <summary>One chart per metric declared by the worker.</summary>
    public ObservableCollection<ChartModel> MetricCharts { get; } = [];

    public ObservableCollection<string> PayloadTypes { get; } = [];

    public ObservableCollection<string> PayloadTargets { get; } = [];

    /// <summary>Set by the view: opens a file dialog, returns the chosen path or null.</summary>
    public Func<Task<string?>>? AudioFilePicker { get; set; }

    /// <summary>Set by the view: opens a file dialog for images and videos.</summary>
    public Func<Task<string?>>? VideoFilePicker { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private SessionProfile? _selectedProfile;

    [ObservableProperty]
    private string _profileDescription = "";

    /// <summary>SessionStart.params for the next session: "key=value key2=value2".</summary>
    [ObservableProperty]
    private string _paramsText = "";

    [ObservableProperty]
    private string _providerState = nameof(AiConnectionState.Stopped);

    [ObservableProperty]
    private string _stateDetail = "No session";

    [ObservableProperty]
    private string _workerInfo = "Worker: -";

    [ObservableProperty]
    private string _capabilitiesText = "Start a session to see the worker capabilities.";

    [ObservableProperty]
    private string _clockText = FormatPts(0);

    [ObservableProperty]
    private string _statsText = "-";

    [ObservableProperty]
    private string _detailText = "-";

    [ObservableProperty]
    private string _latencySummary = "no decision yet";

    [ObservableProperty]
    private string _decisionSummary = "-";

    [ObservableProperty]
    private string _onAirLabel = "-";

    [ObservableProperty]
    private string? _selectedPayloadType;

    [ObservableProperty]
    private string? _selectedPayloadTarget;

    [ObservableProperty]
    private string _payloadJson = "{}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendPayloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    private double NowSeconds => _ai.NowUs / 1_000_000.0;

    /// <summary>Best-effort cleanup on app exit. The worker watchdog covers the rest.</summary>
    public void Shutdown()
    {
        _uiTimer.Stop();
        StopAudioSources();
        StopVideoSources();
        Task.Run(() => _ai.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    // --- ICameraActions -------------------------------------------------------------------

    public void RunAction(CameraViewModel camera, CameraActionProfile action, bool on)
    {
        if (!IsRunning || (on ? action.On : action.Off) is not { } template)
        {
            return;
        }

        try
        {
            var data = template.Data is null ? new Struct() : JsonParser.Default.Parse<Struct>(template.Data.ToJsonString());
            data.Fields["camera_id"] = Value.ForString(camera.Id);
            _ai.SendPayload(template.Type, data);
        }
        catch (Exception ex)
        {
            AddLog("app", $"Action '{action.Label}' failed: {ex.Message}");
        }
    }

    public void TakeOnAir(CameraViewModel camera)
    {
        if (IsRunning)
        {
            _ai.ReportOnAir(camera.Id);
        }
    }

    public async Task PickAudioAsync(CameraViewModel camera)
    {
        if (AudioFilePicker is null || await AudioFilePicker() is not { } path)
        {
            return;
        }

        camera.AudioFile = path;
        if (IsRunning)
        {
            SetAudioSource(camera.AudioChannel, path);
        }
    }

    public void ClearAudio(CameraViewModel camera)
    {
        camera.AudioFile = null;
        SetAudioSource(camera.AudioChannel, null);
    }

    public async Task PickVideoAsync(CameraViewModel camera)
    {
        if (VideoFilePicker is null || await VideoFilePicker() is not { } path)
        {
            return;
        }

        camera.VideoFile = path;
        if (IsRunning)
        {
            SetVideoSource(camera.Id, path);
        }
    }

    public void ClearVideo(CameraViewModel camera)
    {
        camera.VideoFile = null;
        SetVideoSource(camera.Id, null);
    }

    // --- Session ---------------------------------------------------------------------------

    partial void OnSelectedProfileChanged(SessionProfile? value)
    {
        Cameras.Clear();
        if (value is null)
        {
            ProfileDescription = "No profile: add one in profiles/*.json";
            Timeline.SetRows([]);
            return;
        }

        ProfileDescription = $"domain '{value.Domain}' · {value.Cameras.Count} cameras · " +
                             $"{value.AudioChannels} mics · {value.Description}";
        ParamsText = string.Join(" ", value.Params.Select(p => $"{p.Key}={p.Value}"));
        for (var i = 0; i < value.Cameras.Count; i++)
        {
            Cameras.Add(new CameraViewModel(value.Cameras[i], i, value.CameraActions, this));
        }

        Timeline.SetRows(Cameras.Select(c => new TimelineRow(c.Id, c.Label, c.RawColor)));
        PayloadTargets.Clear();
        PayloadTargets.Add(NoCamera);
        foreach (var camera in Cameras)
        {
            PayloadTargets.Add(camera.Id);
        }

        SelectedPayloadTarget = NoCamera;
    }

    private bool CanStart() => !IsRunning && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        IsRunning = true;
        ResetLiveViews();

        var session = new AiSessionOptions
        {
            Domain = profile.Domain,
            Cameras = Cameras.Select(c => c.ToAiCamera()).ToList(),
            Parameters = ParseParams(ParamsText),
            Mode = profile.Mode,
            Arbiter = profile.Arbiter,
            InitialCameraId = profile.InitialCamera,
        };
        try
        {
            await _ai.StartAsync(session);
        }
        catch (Exception ex)
        {
            AddLog("app", $"Start failed: {ex.Message}");
            IsRunning = false;
            return;
        }

        foreach (var camera in Cameras.Where(c => c.HasAudio))
        {
            SetAudioSource(camera.AudioChannel, camera.AudioFile);
        }

        foreach (var camera in Cameras.Where(c => c.VideoFile is not null))
        {
            SetVideoSource(camera.Id, camera.VideoFile);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        foreach (var action in Cameras.SelectMany(c => c.Actions))
        {
            action.IsChecked = false;
        }

        StopAudioSources();
        StopVideoSources();
        await _ai.StopAsync();
        Timeline.Close(NowSeconds);
        foreach (var camera in Cameras)
        {
            camera.ResetLive();
        }

        IsRunning = false;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void SendPayload()
    {
        if (string.IsNullOrWhiteSpace(SelectedPayloadType))
        {
            AddLog("app", "Choose a payload type first");
            return;
        }

        try
        {
            var data = string.IsNullOrWhiteSpace(PayloadJson) ? new Struct() : JsonParser.Default.Parse<Struct>(PayloadJson);
            if (SelectedPayloadTarget is { } target && target != NoCamera)
            {
                data.Fields["camera_id"] = Value.ForString(target);
            }

            _ai.SendPayload(SelectedPayloadType, data);
        }
        catch (Exception ex)
        {
            AddLog("app", $"Invalid payload JSON: {ex.Message}");
        }
    }

    private void ResetLiveViews()
    {
        Decisions.Clear();
        OnAirHistory.Clear();
        _latency.Clear();
        LatencyChart.Clear();
        LatencySummary = _latency.Summary();
        _accepted = 0;
        _rejected = 0;
        _suggested = 0;
        DecisionSummary = "-";
        Timeline.SetRows(Cameras.Select(c => new TimelineRow(c.Id, c.Label, c.RawColor)));
    }

    // --- Worker events -----------------------------------------------------------------------

    private void OnSessionStarted(WorkerCapabilities capabilities)
    {
        WorkerInfo = $"Worker {capabilities.WorkerId} · domain {capabilities.Domain} v{capabilities.Version}";
        CapabilitiesText = Describe(capabilities);

        foreach (var camera in Cameras)
        {
            camera.ApplyCapabilities(capabilities);
        }

        var selectedType = SelectedPayloadType;
        PayloadTypes.Clear();
        foreach (var type in capabilities.PayloadTypes)
        {
            PayloadTypes.Add(type);
        }

        SelectedPayloadType = PayloadTypes.Contains(selectedType ?? "") ? selectedType : PayloadTypes.FirstOrDefault();

        BuildMetricCharts(capabilities);

        // A (re)started worker knows nothing about the manual hints still active.
        foreach (var action in Cameras.SelectMany(c => c.Actions))
        {
            action.Replay();
        }
    }

    private void BuildMetricCharts(WorkerCapabilities capabilities)
    {
        if (_metricCharts.Select(c => c.Metric).SequenceEqual(capabilities.Metrics))
        {
            return; // Restart of the same worker: keep the history.
        }

        _metricCharts.Clear();
        MetricCharts.Clear();
        foreach (var metric in capabilities.Metrics)
        {
            var chart = new ChartModel(metric.Label, metric.Unit, metric.Min, metric.Max);
            if (metric.Scope == MetricDescriptor.Types.Scope.Camera)
            {
                foreach (var camera in Cameras)
                {
                    chart.GetOrAdd(camera.Label, camera.RawColor);
                }
            }
            else
            {
                chart.GetOrAdd(metric.Label, Color.Parse("#E0E0E0"));
            }

            _metricCharts.Add((chart, metric));
            MetricCharts.Add(chart);
        }
    }

    private void OnStats(WorkerStats stats)
    {
        var now = NowSeconds;
        foreach (var camera in Cameras)
        {
            camera.ApplyStats(stats.Cameras.GetValueOrDefault(camera.Id));
        }

        foreach (var (chart, metric) in _metricCharts)
        {
            if (metric.Scope == MetricDescriptor.Types.Scope.Camera)
            {
                foreach (var camera in Cameras)
                {
                    if (stats.Cameras.TryGetValue(camera.Id, out var cameraStats)
                        && cameraStats.Metrics.TryGetValue(metric.Key, out var value))
                    {
                        chart.GetOrAdd(camera.Label, camera.RawColor).Add(now, value);
                    }
                }
            }
            else if (stats.Metrics.TryGetValue(metric.Key, out var value))
            {
                chart.Series[0].Add(now, value);
            }
        }

        LatencyChart.GetOrAdd("inference avg", Color.Parse("#BA68C8")).Add(now, stats.InferenceMsAvg);

        var fps = stats.FpsPerCamera.Count == 0
            ? ""
            : " · fps " + string.Join(" ", stats.FpsPerCamera.Select(f => $"{f.Key}:{f.Value:0}"));
        StatsText = $"inference avg {stats.InferenceMsAvg:0.000} ms · dropped frames {stats.FramesDropped} · " +
                    $"torn frames {stats.FramesTorn}{fps}";
        DetailText = string.IsNullOrEmpty(stats.Detail) ? "-" : stats.Detail;
    }

    private void OnSuggestion(AiSuggestion suggestion)
    {
        var now = NowSeconds;
        var verdict = suggestion.Verdict;
        if (verdict is null)
        {
            _suggested++;
        }
        else if (verdict.Accepted)
        {
            _accepted++;
        }
        else
        {
            _rejected++;
        }

        DecisionSummary = _suggested > 0
            ? $"{_suggested} suggested (suggest mode)"
            : $"{_accepted} accepted · {_rejected} rejected";
        _latency.Add(now, suggestion.LatencyMs);
        LatencySummary = _latency.Summary();
        LatencyChart.GetOrAdd("decision age", Color.Parse("#FFCA28")).Add(now, suggestion.LatencyMs);
        LatencyChart.GetOrAdd("worker processing", Color.Parse("#4FC3F7")).Add(now, suggestion.ProcessingMs);
        Timeline.Mark(now, suggestion.CameraId, verdict?.Accepted);

        foreach (var camera in Cameras)
        {
            camera.Score = suggestion.Scores.GetValueOrDefault(camera.Id);
        }

        var details = new StringBuilder();
        if (suggestion.Scores.Count > 0)
        {
            details.Append("scores ").AppendJoin("  ", suggestion.Scores.Select(s => $"{Label(s.Key)} {s.Value:0.00}"));
        }

        if (suggestion.SceneId is { } sceneId)
        {
            details.Append(" · scene ").Append(sceneId);
        }

        details.Append(" · ").Append(suggestion.Transition.Kind.ToString().ToLowerInvariant());
        if (suggestion.Transition.DurationMs > 0)
        {
            details.Append(' ').Append(suggestion.Transition.DurationMs).Append(" ms");
        }

        details.Append(" · min_hold ").Append(suggestion.MinHoldMs).Append(" ms · id ").Append(suggestion.DecisionId);

        Prepend(Decisions, new DecisionItem(
            FormatPts(suggestion.SourcePtsUs),
            Label(suggestion.CameraId),
            Brush(suggestion.CameraId),
            $"{suggestion.Confidence:0.00}",
            $"{suggestion.LatencyMs:0.0} ms",
            $"{suggestion.ProcessingMs:0.00} ms",
            verdict?.Accepted ?? true,
            verdict is null ? "SUGGESTED" : verdict.Accepted ? "ACCEPTED" : "REJECTED",
            suggestion.Reason,
            verdict?.Reason ?? "suggest mode: the operator decides",
            details.ToString()));
    }

    private void OnAirChanged(AiOnAirChange change)
    {
        foreach (var camera in Cameras)
        {
            camera.IsOnAir = camera.Id == change.CameraId;
        }

        OnAirLabel = $"{Label(change.CameraId)} ({change.Source})";
        Timeline.OnAir(change.CameraId, change.PtsUs / 1_000_000.0, change.Source switch
        {
            AiOnAirSource.Ai => ShotSource.Ai,
            AiOnAirSource.Manual => ShotSource.Manual,
            _ => ShotSource.Initial,
        });
        Prepend(OnAirHistory, new OnAirItem(FormatPts(change.PtsUs), Label(change.CameraId), Brush(change.CameraId), change.Source.ToString()));
    }

    /// <summary>Plays <paramref name="path"/> as microphone <paramref name="channel"/>, or stops it when null.</summary>
    private void SetAudioSource(int channel, string? path)
    {
        if (_audioSources.Remove(channel, out var previous))
        {
            previous.Dispose();
        }

        if (path is null || channel < 0)
        {
            return;
        }

        try
        {
            _audioSources[channel] = new FfmpegAudioSource(path, channel, _ai, line => Ui(() => AddLog("audio", line)));
            AddLog("audio", $"Mic {channel}: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AddLog("audio", $"Mic {channel}: cannot play {path}: {ex.Message}");
        }
    }

    /// <summary>Plays <paramref name="path"/> as camera <paramref name="cameraId"/>, or stops it when null.</summary>
    private void SetVideoSource(string cameraId, string? path)
    {
        if (_videoSources.Remove(cameraId, out var previous))
        {
            previous.Dispose();
        }

        if (path is null)
        {
            return;
        }

        if (_ai.VideoFormat is not { } format)
        {
            AddLog("video", $"{Label(cameraId)}: the worker takes no video");
            return;
        }

        try
        {
            _videoSources[cameraId] = VideoSource.Open(path, cameraId, format, _ai, line => Ui(() => AddLog("video", line)));
            AddLog("video", $"{Label(cameraId)}: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AddLog("video", $"{Label(cameraId)}: cannot play {path}: {ex.Message}");
        }
    }

    private void StopVideoSources()
    {
        foreach (var source in _videoSources.Values)
        {
            source.Dispose();
        }

        _videoSources.Clear();
    }

    private void StopAudioSources()
    {
        foreach (var source in _audioSources.Values)
        {
            source.Dispose();
        }

        _audioSources.Clear();
    }

    private void OnUiTick()
    {
        var now = NowSeconds;
        ClockText = FormatPts(_ai.NowUs);
        Timeline.Advance(now);
        LatencyChart.Advance(now);
        foreach (var chart in MetricCharts)
        {
            chart.Advance(now);
        }
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static string Describe(WorkerCapabilities c)
    {
        var text = new StringBuilder();
        text.AppendLine($"worker      {c.WorkerId} ({c.Domain} v{c.Version})");
        text.AppendLine($"inputs      required [{string.Join(", ", c.RequiredInputs)}] optional [{string.Join(", ", c.OptionalInputs)}]");
        text.AppendLine($"transports  {string.Join(", ", c.Transports)}");
        text.AppendLine($"max cameras {c.MaxCameras}");
        if (c.PreferredVideo is { } video)
        {
            text.AppendLine($"video       {video.Width}x{video.Height} {video.PixelFormat} @ {video.Fps} fps");
        }

        if (c.PreferredAudio is { } audio)
        {
            text.AppendLine($"audio       {audio.SampleRate} Hz, {audio.Channels} ch/mic, {audio.SampleFormat}");
        }

        text.AppendLine($"payloads    {string.Join(", ", c.PayloadTypes)}");
        foreach (var metric in c.Metrics)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"metric      {metric.Key} ({metric.Scope}) {metric.Label} [{metric.Min}..{metric.Max}] {metric.Unit}"));
        }

        foreach (var flag in c.CameraFlags)
        {
            text.AppendLine($"flag        {flag.Label} ({flag.Key}): {flag.Description}");
        }

        return text.ToString().TrimEnd();
    }

    private CameraViewModel? Find(string cameraId) => Cameras.FirstOrDefault(c => c.Id == cameraId);

    private string Label(string cameraId) => Find(cameraId)?.Label ?? cameraId;

    private IBrush Brush(string cameraId) => Find(cameraId)?.Color ?? NeutralBrush;

    private void AddLog(string source, string text) =>
        Prepend(Logs, new LogItem(DateTime.Now.ToString("HH:mm:ss.fff"), source, text));

    private static Dictionary<string, string> ParseParams(string text) =>
        text.Split([' ', ';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

    private static void Prepend<T>(ObservableCollection<T> list, T item)
    {
        list.Insert(0, item);
        if (list.Count > MaxListItems)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    private static string FormatPts(long ptsUs) => TimeSpan.FromMicroseconds(ptsUs).ToString(@"mm\:ss\.fff");

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
