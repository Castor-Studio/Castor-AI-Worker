using Castor.AI.Worker;
using Castor.AI.Worker.Models;
using Castor.AI.Worker.Strategies;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;

// Usage: Castor.AI.Worker --domain podcast|football --pipe <name> [--parent-pid <pid>]
var options = WorkerOptions.Parse(args);
if (!StrategyCatalog.IsKnown(options.Domain))
{
    Console.Error.WriteLine($"Unknown domain '{options.Domain}'. Known: {string.Join(", ", StrategyCatalog.KnownDomains)}");
    return 2;
}

var builder = WebApplication.CreateBuilder();

builder.Logging.ClearProviders();
// Per-request ASP.NET Core logs drown the app log panel.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.ColorBehavior = LoggerColorBehavior.Disabled;
});

builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ListenNamedPipe(options.PipeName, listen => listen.Protocols = HttpProtocols.Http2));

builder.Services.AddGrpc();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkerState>();
builder.Services.AddSingleton<ModelStore>();
builder.Services.AddSingleton<StrategyCatalog>();
builder.Services.AddHostedService<Warmup>();
builder.Services.AddHostedService<ParentWatchdog>();

var app = builder.Build();
app.MapGrpcService<AiWorkerService>();
app.Run();
return Environment.ExitCode;
