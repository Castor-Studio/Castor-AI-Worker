using System.Globalization;

namespace Castor.AI.Worker.Strategies.Football;

/// <summary>Detector thresholds, same defaults as Castor-AI <c>constants.py</c>.</summary>
public sealed record BallDetectorOptions
{
    /// <summary><c>BALL_CONFIDENCE</c>: min ball score to count as found.</summary>
    public float BallConfidence { get; init; } = 0.60f;

    /// <summary><c>PREDICTION_CONFIDENCE</c>: ultralytics <c>conf</c>, min score of a kept detection.</summary>
    public float PredictionConfidence { get; init; } = 0.25f;

    /// <summary>
    /// Plane order given to the model. False (default) = what Python actually feeds: <c>detector.py</c> converts
    /// the frame to RGB, then ultralytics flips numpy input again (it expects BGR), so the model sees BGR.
    /// True = RGB, the order the model was trained on.
    /// </summary>
    public bool SwapRedBlue { get; init; }
}

/// <summary>
/// Football tuning. Python keys (<c>module_config</c>) where they exist, through <c>SessionStart.params</c>.
/// </summary>
public sealed record FootballOptions
{
    /// <summary><c>default_confidence</c>: confidence of every decision.</summary>
    public float DefaultConfidence { get; init; } = 1.0f;

    /// <summary><c>frameskip</c>: detection rounds skipped between two analysed ones.</summary>
    public int Frameskip { get; init; }

    /// <summary>Not in Python (no hold there). Sent as <c>SceneDecision.min_hold_ms</c>; 0 = arbiter default.</summary>
    public uint MinHoldMs { get; init; }

    public BallDetectorOptions Detector { get; init; } = new();

    public static FootballOptions FromParams(IReadOnlyDictionary<string, string> parameters)
    {
        var defaults = new FootballOptions();
        return new FootballOptions
        {
            DefaultConfidence = (float)Double("default_confidence", defaults.DefaultConfidence),
            Frameskip = Math.Max(0, (int)Double("frameskip", defaults.Frameskip)),
            MinHoldMs = (uint)Math.Max(0, Double("min_hold_ms", defaults.MinHoldMs)),
            Detector = new BallDetectorOptions
            {
                BallConfidence = (float)Double("ball_confidence", defaults.Detector.BallConfidence),
                PredictionConfidence = (float)Double("prediction_confidence", defaults.Detector.PredictionConfidence),
                SwapRedBlue = Bool("swap_red_blue", defaults.Detector.SwapRedBlue),
            },
        };

        double Double(string key, double fallback) =>
            parameters.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        bool Bool(string key, bool fallback) =>
            parameters.TryGetValue(key, out var raw) ? raw.Trim().ToLowerInvariant() is "true" or "1" : fallback;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"default_confidence={DefaultConfidence:0.00} frameskip={Frameskip} min_hold_ms={MinHoldMs} " +
        $"ball_confidence={Detector.BallConfidence:0.00} prediction_confidence={Detector.PredictionConfidence:0.00} " +
        $"swap_red_blue={Detector.SwapRedBlue}");
}
