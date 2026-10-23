# Castor.AI.Worker

Directing AI of Castor Studio: picks, live, the camera to put on air, from the camera video, the microphone
audio and the hints sent by the app. Castor Studio lives in its own repository and uses this one through the
client SDK and the worker executable.

| Project | Role |
|---|---|
| `Castor.AI` | Client SDK referenced by Castor Studio (`AiClient`): starts the worker, sends it audio and video, surfaces the suggested shots. See [Castor.AI/README.md](Castor.AI/README.md). |
| `Castor.AI.Contracts` | gRPC contract (`castor_ai.proto`) and shared memory layouts, common to the SDK and the worker. |
| `Castor.AI.Worker` | Worker process, one domain per process: `podcast` (Silero VAD), `football` (ball detection, YOLO). |
| `Castor.AI.Dashboard` | Avalonia debug console for the AI team, driven by `profiles/*.json`. |
| `Castor.AI.Tests`, `Castor.AI.Worker.Tests` | xUnit tests, including parity with the Castor-AI Python modules. |

Architecture, shared memory layout, per-domain strategies and model management:
[docs/architecture.md](docs/architecture.md).

How the worker works and how the Avalonia app talks to it: DocFX site in `docs/`
([guides/worker.md](docs/guides/worker.md), [guides/avalonia.md](docs/guides/avalonia.md)) with the SDK API
reference, built locally with `pwsh eng/Build-Docs.ps1 -Serve` (`http://localhost:8080`).

## Getting started

.NET 10 SDK on Windows. ffmpeg (in the `PATH` or `CASTOR_FFMPEG`) is only needed by the debug console,
to read test files.

```
dotnet test Castor.AI.Worker.sln
dotnet run --project Castor.AI.Dashboard
```

No script to run for the models: the worker (and the tests) download the missing ones from the `models-v1`
release of this repository, checked against their SHA-256. Publishing a new model: `scripts/publish-models.ps1`
(see `docs/architecture.md`, section 9).
