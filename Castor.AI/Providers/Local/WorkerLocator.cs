namespace Castor.AI.Providers.Local;

/// <summary>
/// Finds the worker executable: <see cref="LocalWorkerOptions.WorkerPath"/>, <c>CASTOR_WORKER_PATH</c>,
/// next to the app, or the sibling project output in the dev layout (&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;/).
/// </summary>
internal static class WorkerLocator
{
    private const string EnvironmentVariable = "CASTOR_WORKER_PATH";
    private const string ProjectName = "Castor.AI.Worker";

    public static string Find(string? configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath))
        {
            return File.Exists(configuredPath)
                ? configuredPath
                : throw new FileNotFoundException($"Worker not found at {configuredPath}", configuredPath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var executable = OperatingSystem.IsWindows() ? $"{ProjectName}.exe" : ProjectName;

        // The build copies the worker apphost next to the app without its dll
        // (ReferenceOutputAssembly=false), so only accept a complete install.
        var sideBySide = Path.Combine(AppContext.BaseDirectory, executable);
        if (IsComplete(sideBySide))
        {
            return sideBySide;
        }

        var tfmDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configurationDirectory = tfmDirectory.Parent;
        var solutionDirectory = configurationDirectory?.Parent?.Parent?.Parent;
        if (configurationDirectory is not null && solutionDirectory is not null)
        {
            var devLayout = Path.Combine(
                solutionDirectory.FullName, ProjectName, "bin", configurationDirectory.Name, tfmDirectory.Name, executable);
            if (IsComplete(devLayout))
            {
                return devLayout;
            }
        }

        throw new FileNotFoundException(
            $"{executable} not found. Build the solution or set {EnvironmentVariable}.");
    }

    private static bool IsComplete(string executablePath) =>
        File.Exists(executablePath)
        && File.Exists(Path.Combine(Path.GetDirectoryName(executablePath)!, $"{ProjectName}.dll"));
}
