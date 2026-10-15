using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Castor.AI;

namespace Castor.Proto.App.Profiles;

/// <summary>
/// Everything domain-specific the app needs to run a session, so the UI itself stays generic:
/// which worker domain, which cameras, director params, arbiter rules, and the per-camera
/// buttons that send domain payloads. Loaded from profiles/*.json.
/// </summary>
public sealed record SessionProfile
{
    public string Name { get; init; } = "";

    public string Domain { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>SessionStart.params, domain-specific (e.g. podcast min_hold_time).</summary>
    public Dictionary<string, string> Params { get; init; } = [];

    /// <summary>"auto": the SDK arbiter cuts. "suggest": suggestions only, the operator cuts.</summary>
    public AiDecisionMode Mode { get; init; } = AiDecisionMode.Auto;

    public ArbiterOptions Arbiter { get; init; } = new();

    /// <summary>Camera on air when the session starts. Default: first camera.</summary>
    public string? InitialCamera { get; init; }

    public List<CameraProfile> Cameras { get; init; } = [];

    /// <summary>Buttons shown on each camera card.</summary>
    public List<CameraActionProfile> CameraActions { get; init; } = [];

    [JsonIgnore]
    public string SourcePath { get; init; } = "";

    public int AudioChannels => Cameras.Count == 0 ? 0 : Math.Max(0, Cameras.Max(c => c.AudioChannel) + 1);

    public override string ToString() => Name;
}

public sealed record CameraProfile
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Role { get; init; } = "";

    /// <summary>Microphone channel in the audio ring, -1 = none.</summary>
    public int AudioChannel { get; init; } = -1;

    /// <summary>Played on <see cref="AudioChannel"/>. Relative to the profile file.</summary>
    public string? AudioFile { get; init; }

    /// <summary>Test video of this camera: a still image or any file ffmpeg reads. Relative to the profile file.</summary>
    public string? VideoFile { get; init; }
}

/// <summary>
/// A payload sent for one camera; <c>camera_id</c> is added to <see cref="PayloadTemplate.Data"/>.
/// With <see cref="Off"/> the action is a toggle (On when checked, Off when unchecked).
/// </summary>
public sealed record CameraActionProfile
{
    public string Label { get; init; } = "";

    public string Tooltip { get; init; } = "";

    /// <summary>Only on cameras that have a microphone.</summary>
    public bool RequiresAudio { get; init; }

    public PayloadTemplate On { get; init; } = new();

    public PayloadTemplate? Off { get; init; }
}

public sealed record PayloadTemplate
{
    public string Type { get; init; } = "";

    public JsonObject? Data { get; init; }
}

public static class ProfileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads profiles/*.json from the closest profiles folder above the app.</summary>
    public static List<SessionProfile> LoadAll(Action<string> log)
    {
        var directory = FindProfilesDirectory();
        if (directory is null)
        {
            log("No profiles folder found");
            return [];
        }

        log($"Profiles: {directory}");
        var profiles = new List<SessionProfile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            try
            {
                var profile = JsonSerializer.Deserialize<SessionProfile>(File.ReadAllText(file), JsonOptions)
                              ?? throw new InvalidDataException("empty profile");
                Validate(profile);
                profiles.Add(Resolve(profile, file));
            }
            catch (Exception ex)
            {
                log($"Profile {Path.GetFileName(file)} ignored: {ex.Message}");
            }
        }

        return profiles;
    }

    private static void Validate(SessionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Domain))
        {
            throw new InvalidDataException("domain is required");
        }

        if (profile.Cameras.Count == 0)
        {
            throw new InvalidDataException("at least one camera is required");
        }

        var duplicate = profile.Cameras.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"duplicate camera id '{duplicate.Key}'");
        }
    }

    private static SessionProfile Resolve(SessionProfile profile, string file)
    {
        var baseDirectory = Path.GetDirectoryName(file)!;
        return profile with
        {
            Name = string.IsNullOrWhiteSpace(profile.Name) ? Path.GetFileNameWithoutExtension(file) : profile.Name,
            SourcePath = file,
            Cameras = profile.Cameras
                .Select(c => c with { AudioFile = Absolute(c.AudioFile), VideoFile = Absolute(c.VideoFile) })
                .ToList(),
        };

        string? Absolute(string? path) => path is null ? null : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static string? FindProfilesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            // Must hold JSON files: on Windows this source folder (Profiles/) also matches "profiles".
            var profiles = Path.Combine(directory.FullName, "profiles");
            if (Directory.Exists(profiles) && Directory.EnumerateFiles(profiles, "*.json").Any())
            {
                return profiles;
            }
        }

        return null;
    }
}
