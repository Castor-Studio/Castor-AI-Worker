namespace Castor.AI.Worker;

public sealed record WorkerOptions(string Domain, string PipeName, int? ParentPid)
{
    public static WorkerOptions Parse(string[] args)
    {
        var domain = "podcast";
        var pipeName = "castor-ai-worker";
        int? parentPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

        return new WorkerOptions(domain, pipeName, parentPid);
    }
}
