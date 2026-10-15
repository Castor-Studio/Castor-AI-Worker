using System.Diagnostics;

namespace Castor.AI;

/// <summary>Master session clock: microseconds since session start, monotonic.</summary>
internal sealed class SessionClock
{
    private readonly Stopwatch _stopwatch = new();

    public long NowUs => _stopwatch.Elapsed.Ticks / TimeSpan.TicksPerMicrosecond;

    public void Restart() => _stopwatch.Restart();

    public void Stop() => _stopwatch.Stop();
}
