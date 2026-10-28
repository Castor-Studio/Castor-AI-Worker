namespace Castor.AI.Dashboard.Diagnostics;

public sealed record DeviceDiagnosticResult(string Model, string RequestedDevice, string Provider,
    double MeanMs, double P50Ms, double P95Ms, Dictionary<string, int> KernelPlacements,
    float? MaxDifferenceFromCpu, string OutputComparison)
{
    public string KernelPlacementText => string.Join(", ", KernelPlacements.Select(pair => $"{pair.Key}: {pair.Value}"));
}
