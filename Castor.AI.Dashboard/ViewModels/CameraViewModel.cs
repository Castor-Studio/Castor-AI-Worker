using System.Collections.ObjectModel;
using Avalonia.Media;
using Castor.AI;
using Castor.AI.Contracts.V1;
using Castor.AI.Dashboard.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Castor.AI.Dashboard.ViewModels;

/// <summary>What a camera card can ask the main view model to do.</summary>
public interface ICameraActions
{
    void RunAction(CameraViewModel camera, CameraActionProfile action, bool on);

    void TakeOnAir(CameraViewModel camera);

    Task PickAudioAsync(CameraViewModel camera);

    void ClearAudio(CameraViewModel camera);

    Task PickVideoAsync(CameraViewModel camera);

    void ClearVideo(CameraViewModel camera);
}

/// <summary>
/// One camera card. Everything domain-specific comes from the profile (actions) or from the
/// worker capabilities (metrics, flags), so the card works for any AI model.
/// </summary>
public sealed partial class CameraViewModel : ObservableObject
{
    private readonly ICameraActions _actions;
    private Dictionary<string, FlagDescriptor> _flagDescriptors = [];

    public CameraViewModel(CameraProfile profile, int index, IEnumerable<CameraActionProfile> actions, ICameraActions host)
    {
        _actions = host;
        Id = profile.Id;
        Label = string.IsNullOrEmpty(profile.Label) ? profile.Id : profile.Label;
        Role = profile.Role;
        AudioChannel = profile.AudioChannel;
        Color = new SolidColorBrush(Charts.Palette.At(index));
        RawColor = Charts.Palette.At(index);
        _audioFile = profile.AudioFile is { } file && File.Exists(file) ? file : null;
        _videoFile = profile.VideoFile is { } video && File.Exists(video) ? video : null;
        foreach (var action in actions.Where(a => !a.RequiresAudio || HasAudio))
        {
            Actions.Add(new CameraActionViewModel(this, action, host));
        }
    }

    public string Id { get; }

    public string Label { get; }

    public string Role { get; }

    public int AudioChannel { get; }

    public IBrush Color { get; }

    public Color RawColor { get; }

    public bool HasAudio => AudioChannel >= 0;

    public string Description =>
        string.Join(" · ", new[] { Id, Role.Length > 0 ? $"role {Role}" : "role from label", HasAudio ? $"mic {AudioChannel}" : null }
            .Where(part => part is not null));

    public ObservableCollection<CameraActionViewModel> Actions { get; } = [];

    /// <summary>Camera-scope metrics declared by the worker.</summary>
    public ObservableCollection<MetricViewModel> Metrics { get; } = [];

    /// <summary>Flags raised by the worker for this camera.</summary>
    public ObservableCollection<FlagViewModel> Flags { get; } = [];

    [ObservableProperty]
    private bool _isOnAir;

    /// <summary>Score of this camera in the last decision.</summary>
    [ObservableProperty]
    private double _score;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioFileName))]
    private string? _audioFile;

    public string AudioFileName => AudioFile is null ? "silence" : Path.GetFileName(AudioFile);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoFileName))]
    private string? _videoFile;

    public string VideoFileName => VideoFile is null ? "none" : Path.GetFileName(VideoFile);

    public AiCamera ToAiCamera() => new(Id, Label, Role, AudioChannel);

    public void ApplyCapabilities(WorkerCapabilities capabilities)
    {
        Metrics.Clear();
        foreach (var metric in capabilities.Metrics.Where(m => m.Scope == MetricDescriptor.Types.Scope.Camera))
        {
            Metrics.Add(new MetricViewModel(metric));
        }

        _flagDescriptors = capabilities.CameraFlags.ToDictionary(f => f.Key);
        Flags.Clear();
    }

    public void ApplyStats(CameraStats? stats)
    {
        foreach (var metric in Metrics)
        {
            metric.Value = stats is not null && stats.Metrics.TryGetValue(metric.Key, out var value) ? value : null;
        }

        var raised = stats?.Flags ?? [];
        if (Flags.Select(f => f.Key).SequenceEqual(raised))
        {
            return;
        }

        Flags.Clear();
        foreach (var key in raised)
        {
            Flags.Add(_flagDescriptors.TryGetValue(key, out var descriptor)
                ? new FlagViewModel(key, descriptor.Label, descriptor.Description)
                : new FlagViewModel(key, key.ToUpperInvariant(), ""));
        }
    }

    public void ResetLive()
    {
        Score = 0;
        ApplyStats(null);
    }

    [RelayCommand]
    private void TakeOnAir() => _actions.TakeOnAir(this);

    [RelayCommand]
    private Task PickAudio() => _actions.PickAudioAsync(this);

    [RelayCommand]
    private void ClearAudio() => _actions.ClearAudio(this);

    [RelayCommand]
    private Task PickVideo() => _actions.PickVideoAsync(this);

    [RelayCommand]
    private void ClearVideo() => _actions.ClearVideo(this);
}

public sealed partial class MetricViewModel(MetricDescriptor descriptor) : ObservableObject
{
    public string Key => descriptor.Key;

    public string Label => descriptor.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fraction))]
    [NotifyPropertyChangedFor(nameof(Text))]
    private double? _value;

    public double Fraction => Value is { } v && descriptor.Max > descriptor.Min
        ? Math.Clamp((v - descriptor.Min) / (descriptor.Max - descriptor.Min), 0, 1)
        : 0;

    public string Text => Value is { } v ? $"{v:0.##} {descriptor.Unit}".Trim() : "-";
}

public sealed record FlagViewModel(string Key, string Label, string Description);

/// <summary>Profile-defined payload button (or toggle) on a camera card.</summary>
public sealed partial class CameraActionViewModel(CameraViewModel camera, CameraActionProfile action, ICameraActions host)
    : ObservableObject
{
    public string Label => action.Label;

    public string Tooltip => string.IsNullOrEmpty(action.Tooltip) ? $"Sends {action.On.Type}" : action.Tooltip;

    public bool IsToggle => action.Off is not null;

    public bool IsButton => !IsToggle;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Re-sends the current toggle state (a restarted worker knows nothing).</summary>
    public void Replay()
    {
        if (IsToggle && IsChecked)
        {
            host.RunAction(camera, action, on: true);
        }
    }

    [RelayCommand]
    private void Run() => host.RunAction(camera, action, on: true);

    partial void OnIsCheckedChanged(bool value) => host.RunAction(camera, action, value);
}
