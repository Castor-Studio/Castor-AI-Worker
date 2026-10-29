namespace Castor.AI.Worker;

public sealed record WorkerOptions(string Domain, string PipeName, int? ParentPid, string Device = "gpu", bool Diagnostics = false, int GpuAdapter = 0)
{
    public static WorkerOptions Parse(string[] args)
    {
        var domain = "podcast";
        var pipeName = "castor-ai-worker";
        int? parentPid = null;
        var device = "gpu";
        var diagnostics = false;
        var gpuAdapter = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--diagnostics":
                    diagnostics = true;
                    break;
                case "--gpu-adapter":
                    if (++i >= args.Length || !int.TryParse(args[i], out gpuAdapter) || gpuAdapter < 0)
                        throw new ArgumentException("--gpu-adapter requires a non-negative integer.");
                    break;
                case "--device":
                    if (++i >= args.Length || args[i].ToLowerInvariant() is not ("gpu" or "cpu"))
                        throw new ArgumentException("--device requires 'gpu' or 'cpu'.");
                    device = args[i].ToLowerInvariant();
                    break;
                case "--domain" when i + 1 < args.Length:
                    domain = args[++i];
                    break;
                case "--pipe" when i + 1 < args.Length:
                    pipeName = args[++i];
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    parentPid = pid;
                    i++;
                    break;
            }
        }

        return new WorkerOptions(domain, pipeName, parentPid, device, diagnostics, gpuAdapter);
    }
}
