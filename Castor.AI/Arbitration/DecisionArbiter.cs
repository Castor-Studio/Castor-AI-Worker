using Castor.AI.Contracts.V1;

namespace Castor.AI.Arbitration;

/// <summary>
/// Final authority on what goes on air in <see cref="AiDecisionMode.Auto"/>. Worker decisions are
/// suggestions; the arbiter applies staleness, confidence, min hold and manual lock rules.
/// Not thread-safe: callers serialize access.
/// </summary>
internal sealed class DecisionArbiter(ArbiterOptions options)
{
    private long MaxDecisionAgeUs => options.MaxDecisionAgeMs * 1000L;

    private long ManualLockUs => options.ManualLockMs * 1000L;

    public string? OnAir { get; private set; }

    private long _onAirSinceUs;
    private uint _onAirHoldMs;
    private long _manualUntilUs;

    public void Reset(string camera, long nowUs)
    {
        OnAir = camera;
        _onAirSinceUs = nowUs;
        _onAirHoldMs = 0;
        _manualUntilUs = 0;
    }

    public AiVerdict Evaluate(SceneDecision decision, long nowUs)
    {
        var ageMs = (nowUs - decision.SourcePtsUs) / 1000.0;

        if (decision.CameraId == OnAir)
        {
            return new AiVerdict(false, "Already on air", ageMs);
        }

        if (nowUs - decision.SourcePtsUs > MaxDecisionAgeUs)
        {
            return new AiVerdict(false, $"Stale ({ageMs:0} ms > {options.MaxDecisionAgeMs} ms)", ageMs);
        }

        if (decision.Confidence < options.MinConfidence)
        {
            return new AiVerdict(false, $"Confidence {decision.Confidence:0.00} < {options.MinConfidence:0.00}", ageMs);
        }

        if (nowUs < _manualUntilUs)
        {
            return new AiVerdict(false, $"Manual lock ({(_manualUntilUs - nowUs) / 1_000_000.0:0.0} s left)", ageMs);
        }

        var heldMs = (nowUs - _onAirSinceUs) / 1000;
        if (heldMs < _onAirHoldMs)
        {
            return new AiVerdict(false, $"Current shot held {heldMs} ms < min_hold {_onAirHoldMs} ms", ageMs);
        }

        OnAir = decision.CameraId;
        _onAirSinceUs = nowUs;
        _onAirHoldMs = decision.MinHoldMs > 0 ? decision.MinHoldMs : options.DefaultMinHoldMs;
        return new AiVerdict(true, "Accepted", ageMs);
    }

    public void TakeManual(string camera, long nowUs)
    {
        OnAir = camera;
        _onAirSinceUs = nowUs;
        _onAirHoldMs = 0;
        _manualUntilUs = nowUs + ManualLockUs;
    }
}
