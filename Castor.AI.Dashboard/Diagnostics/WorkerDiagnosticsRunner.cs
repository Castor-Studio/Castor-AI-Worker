using System.Diagnostics;
using System.Text.Json;
using Castor.AI.Providers.Local;

namespace Castor.AI.Dashboard.Diagnostics;

/// <summary>Runs isolated worker diagnostics and forwards progress into the dashboard log.</summary>
internal static class WorkerDiagnosticsRunner
{
    private const string ResultPrefix = "CASTOR_DIAGNOSTICS:";

    public static async Task<IReadOnlyList<DeviceDiagnosticResult>> RunAsync(
        string? workerPath, int gpuAdapter, Action<string> report, CancellationToken cancellationToken)
    {
        var executable = WorkerLocator.Find(workerPath);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--diagnostics");
        start.ArgumentList.Add("--gpu-adapter");
        start.ArgumentList.Add(gpuAdapter.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        string? resultJson = null;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith(ResultPrefix, StringComparison.Ordinal))
                resultJson = e.Data[ResultPrefix.Length..];
            else
                report(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) report(e.Data); };

        if (!process.Start()) throw new InvalidOperationException("The diagnostic worker could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        // DataReceived events can trail process exit by a few milliseconds.
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Diagnostic worker exited with code {process.ExitCode}.");
        if (resultJson is null)
            throw new InvalidDataException("The worker exited without returning diagnostic results.");

        return JsonSerializer.Deserialize<List<DeviceDiagnosticResult>>(resultJson,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("The worker returned empty diagnostic results.");
    }

}
