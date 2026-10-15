using System.Globalization;

namespace Castor.AI.Worker.Strategies.Podcast;

/// <summary>
/// Director tuning. Same keys and defaults as the Python <c>PodcastModule</c>
/// (<c>module_config</c>), passed here through <c>SessionStart.params</c>.
/// </summary>
public sealed record PodcastOptions
{
    /// <summary>Min seconds on a shot before the director cuts again.</summary>
    public double MinHoldTime { get; init; } = 2.5;

    /// <summary>Seconds of uninterrupted speech before switching to the speaker's zoom camera.</summary>
    public double MonologueTime { get; init; } = 5.0;

    public float VadThreshold { get; init; } = 0.5f;

    public int MinSilenceDurationMs { get; init; } = 400;

    public int SpeechPadMs { get; init; } = 100;

    /// <summary>Backchannel filter: continuous speech required before a speaker counts.</summary>
    public int MinSpeechConfirmMs { get; init; } = 250;

    /// <summary>Speakers this many dB below the loudest active one are treated as mic bleed.</summary>
    public double CrossGatingThresholdDb { get; init; } = 5.0;

    /// <summary>Sustained simultaneous speech required before cutting to wide.</summary>
    public int DebateConfirmMs { get; init; }

    /// <summary>Silence before falling back to the wide shot.</summary>
    public double SilenceHoldTime { get; init; } = 3.0;

    /// <summary>Speaker switches within <see cref="PingPongWindowSec"/> that force wide. 0 = disabled.</summary>
    public int PingPongThreshold { get; init; }

    public double PingPongWindowSec { get; init; } = 6.0;

    /// <summary>
    /// Not in Python. Mic level smoothing (ms) used by cross-gating; 0 = last chunk only (Python behavior).
    /// See <see cref="Audio.SpeechActivityTracker"/>.
    /// </summary>
    public int LevelSmoothingMs { get; init; }

    public static PodcastOptions FromParams(IReadOnlyDictionary<string, string> parameters)
    {
        var defaults = new PodcastOptions();
        return new PodcastOptions
        {
            MinHoldTime = Double("min_hold_time", defaults.MinHoldTime),
            MonologueTime = Double("monologue_time", defaults.MonologueTime),
            VadThreshold = (float)Double("vad_threshold", defaults.VadThreshold),
            MinSilenceDurationMs = Int("min_silence_duration_ms", defaults.MinSilenceDurationMs),
            SpeechPadMs = Int("speech_pad_ms", defaults.SpeechPadMs),
            MinSpeechConfirmMs = Int("min_speech_confirm_ms", defaults.MinSpeechConfirmMs),
            CrossGatingThresholdDb = Double("cross_gating_threshold_db", defaults.CrossGatingThresholdDb),
            DebateConfirmMs = Int("debate_confirm_ms", defaults.DebateConfirmMs),
            SilenceHoldTime = Double("silence_hold_time", defaults.SilenceHoldTime),
            PingPongThreshold = Int("ping_pong_threshold", defaults.PingPongThreshold),
            PingPongWindowSec = Double("ping_pong_window_sec", defaults.PingPongWindowSec),
            LevelSmoothingMs = Int("level_smoothing_ms", defaults.LevelSmoothingMs),
        };

        double Double(string key, double fallback) =>
            parameters.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        int Int(string key, int fallback) =>
            parameters.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"min_hold_time={MinHoldTime:0.0} monologue_time={MonologueTime:0.0} vad_threshold={VadThreshold:0.00} " +
        $"min_silence_duration_ms={MinSilenceDurationMs} speech_pad_ms={SpeechPadMs} " +
        $"min_speech_confirm_ms={MinSpeechConfirmMs} cross_gating_threshold_db={CrossGatingThresholdDb:0.0} " +
        $"debate_confirm_ms={DebateConfirmMs} silence_hold_time={SilenceHoldTime:0.0} " +
        $"ping_pong_threshold={PingPongThreshold} ping_pong_window_sec={PingPongWindowSec:0.0} " +
        $"level_smoothing_ms={LevelSmoothingMs}");
}
