---
_layout: landing
---

# Castor AI documentation

Castor AI is the directing AI of Castor Studio: a **worker** process picks, live, the camera to put on air,
from the camera video, the microphone audio and the hints sent by the app. The Avalonia app (Castor Studio,
or the `Castor.AI.Dashboard` debug console) only talks to the worker through the `Castor.AI` SDK. This site
is built locally from the repository; nothing is hosted.

- [How the worker works](guides/worker.md): launch, startup and models, gRPC service, session flow,
  per-domain strategies, failures.
- [Talking to the worker from the Avalonia app](guides/avalonia.md): registering `AiClient`, opening a
  session, pushing audio, video and payloads, handling suggestions on the UI thread, lifecycle and deployment.
- [Architecture and formats](architecture.md) (French): local / cloud overview, sequence diagram, real time,
  shared memory layout, Podcast and Football workers, model hosting.
- [API reference](api/Castor.AI.yml): the public C# API of the SDK (`Castor.AI`) and of the contract
  (`Castor.AI.Contracts`), generated from the XML doc comments.

The repository overview and the step-by-step Castor Studio integration are also in the
[README](https://github.com/Castor-Studio/Castor-AI-Worker#readme) and the
[SDK README](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md).

```
Avalonia app (Castor Studio, Castor.AI.Dashboard)
  └ AiClient (Castor.AI SDK)            session, clock, arbiter, audio and video feeds
      └ LocalWorkerProvider             starts and supervises the worker
          ├ gRPC over a named pipe      control, payloads, suggestions, stats, logs
          └ shared memory               video frames (1 region per camera) + audio ring
              └ Castor.AI.Worker.exe    one domain per process: podcast, football
```

## Building this site

Requirements: .NET 10 SDK and PowerShell 7. From the repository root:

```powershell
pwsh eng/Build-Docs.ps1 -Serve
```

The site is served on `http://localhost:8080`. Without `-Serve`, it is only written to `docs/_site/`.
DocFX compiles `Castor.AI` and `Castor.AI.Contracts` to generate the API reference.

Generated folders (`docs/api/`, `docs/_site/`) are not committed.
