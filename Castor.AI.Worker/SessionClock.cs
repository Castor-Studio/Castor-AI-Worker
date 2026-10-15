using System.Diagnostics;

namespace Castor.AI.Worker;

/// <summary>
/// Worker-side estimate of the app session clock.
/// The app owns the clock; every input carries its pts. Between inputs the
/// worker extrapolates with a local stopwatch.
/// </summary>
internal sealed class SessionClock
{
    private readonly Lock _gate = new();
    private long _anchorPtsUs;
    private long _anchorTimestamp = Stopwatch.GetTimestamp();

    public long NowUs
    {
        get
        {
            lock (_gate)
            {
                return EstimateUnlocked();
            }
        }
    }

    /// <summary>Re-anchors on a pts received from the app. Never goes backwards.</summary>
    public void Observe(long ptsUs)
    {
        lock (_gate)
        {
            if (ptsUs > EstimateUnlocked())
            {
                _anchorPtsUs = ptsUs;
                _anchorTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private long EstimateUnlocked() =>
        _anchorPtsUs + Stopwatch.GetElapsedTime(_anchorTimestamp).Ticks / TimeSpan.TicksPerMicrosecond;
}
