# Talking to the worker from the Avalonia app

The Avalonia app (Castor Studio, or the `Castor.AI.Dashboard` debug console) **never** talks to the worker
directly: no gRPC, no shared memory, no process handling. Everything goes through <xref:Castor.AI.AiClient>,
the facade of the `Castor.AI` SDK, which has no UI dependency.

```
Avalonia view ─ binding ─ ViewModel
                           │ StartAsync, ReportOnAir, WriteAudio, WriteVideo, SendPayload
                           ▼
                        AiClient ── session clock, arbiter (Auto mode), AudioFeed, VideoFeed
                           │ IAiProvider
                           ▼
                        LocalWorkerProvider ── starts Castor.AI.Worker.exe, supervises, restarts
                           │ gRPC (named pipe)              │ shared memory
                           ▼                                ▼
                        AiWorker.Session              video regions + audio ring
                           │
                           ▼
                        Castor.AI.Worker
                           │ SceneDecision, WorkerStats, WorkerLog
                           ▼
                        AiClient events ── Dispatch ──► Avalonia UI thread
```

Full reference: `Castor.AI.Dashboard/ViewModels/MainViewModel.cs`, which only uses the AI through this SDK,
exactly like Castor Studio.

## 1. Reference the SDK

Castor Studio references the `Castor.AI` package (GitHub Packages `Castor-Studio`, `Castor.AI.Contracts`
comes as a dependency); the NuGet source and token are described in the
[SDK README](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md#nuget-package).

```xml
<PackageReference Include="Castor.AI" Version="1.0.0" />
```

In this repository, `Castor.AI.Dashboard` references the projects directly.

## 2. Register `AiClient` and the UI thread

SDK events are raised from its own threads (gRPC stream reader, supervision). An Avalonia app can only touch
its controls and bound properties from the UI thread: `AiClientOptions.Dispatch` routes **every** event to it.

With dependency injection (Castor Studio):

```csharp
services.AddCastorAI(options =>
{
    options.Worker = new LocalWorkerOptions
    {
        // Worker shipped in ai/ next to the app (see "Deployment").
        WorkerPath = Path.Combine(AppContext.BaseDirectory, "ai", "Castor.AI.Worker.exe"),
    };
    options.Client = new AiClientOptions { Dispatch = action => Dispatcher.UIThread.Post(action) };
});
```

`AddCastorAI` registers `LocalWorkerOptions`, `IAiProvider` (<xref:Castor.AI.Providers.Local.LocalWorkerProvider>)
and `AiClient` as singletons: inject `AiClient` into the ViewModel.

Without a container (`Castor.AI.Dashboard`):

```csharp
_ai = new AiClient(new LocalWorkerProvider(), new AiClientOptions { Dispatch = Ui });

private static void Ui(Action action)
{
    if (Dispatcher.UIThread.CheckAccess()) action();
    else Dispatcher.UIThread.Post(action);
}
```

Without `WorkerPath`, the SDK looks for the worker in `CASTOR_WORKER_PATH`, then next to the app, then in the
output of the `Castor.AI.Worker` project (dev layout `bin/<config>/<tfm>/`).

<xref:Castor.AI.Providers.Local.LocalWorkerOptions>:

| Option | Default | Role |
|---|---|---|
| `WorkerPath` | lookup above | Path of `Castor.AI.Worker.exe`. |
| `ReadyTimeout` | 10 s | Wait for `Health = READY`, counted from the last progress report (a long model download does not time out). |
| `RestartBackoff` | 1 s, 2 s, 5 s | Delays before reopening a session after a crash; the last one repeats. |
| `AudioRingSeconds` | 2 | Size of the shared memory audio ring. |

## 3. Subscribe to events

Do it once, before `StartAsync`. With `Dispatch`, handlers run on the UI thread.

```csharp
_ai.StateChanged += (_, change) => AiStatusText = $"{change.State}: {change.Detail}";
_ai.SessionStarted += (_, capabilities) => BuildCameraGauges(capabilities.Metrics, capabilities.CameraFlags);
_ai.SuggestionReceived += (_, suggestion) => OnSuggestion(suggestion);
_ai.OnAirChanged += (_, change) => OnAirChanged(change);
_ai.StatsReceived += (_, stats) => UpdateGauges(stats);
_ai.LogReceived += (_, entry) => AddLog(entry.Source, entry.Message);
```

| Event | Source worker message | Use in the app |
|---|---|---|
| `StateChanged` (<xref:Castor.AI.AiStateChange>) | `Health`, process, stream | AI status: `Starting` (with model download progress), `Running`, `Faulted`, `Restarting`, `Stopped`. |
| `SessionStarted` (`WorkerCapabilities`) | `SessionAccepted` | Adapt the UI: metrics, flags, accepted payloads, `VideoFormat`. Raised again after every restart. |
| `SuggestionReceived` (<xref:Castor.AI.AiSuggestion>) | `SceneDecision` | Suggest mode: show camera, confidence, reason. Auto mode: `Verdict` says whether the arbiter took it. |
| `OnAirChanged` (<xref:Castor.AI.AiOnAirChange>) | SDK arbiter | `Source == Ai`: **the app must switch** to `CameraId`. `Initial`, `Manual`: informational. |
| `StatsReceived` (`WorkerStats`) | `WorkerStats` (every 250 ms) | Gauges and charts, keys described by `Capabilities.Metrics` and `CameraFlags`. |
| `LogReceived` (<xref:Castor.AI.AiLogEntry>) | `WorkerLog`, `SessionError`, `SessionEnded`, worker stdout | Log panel. `Source`: `worker`, `local`, `audio` or `client`. |

## 4. Start a session

One AI camera = one Studio scene. The `Id` is free-form (here the scene `Guid`) and comes back as is in the
suggestions.

```csharp
var session = new AiSessionOptions
{
    Domain = "football",                                   // or "podcast"
    Mode = AiDecisionMode.Suggest,                          // or Auto
    Cameras = scenes
        .Select(scene => new AiCamera(scene.Id.ToString("N"), scene.Name, Role: "", AudioChannel: -1))
        .ToList(),
    InitialCameraId = currentScene.Id.ToString("N"),
    Parameters = new Dictionary<string, string> { ["min_hold_ms"] = "2000" },
};

try
{
    await _ai.StartAsync(session);
}
catch (Exception ex)
{
    // Worker not found, not READY in time, or session refused (SessionError): stay manual.
    AiStatusText = ex.Message;
}
```

`StartAsync` starts the worker, waits for `READY`, creates the shared memory regions, opens the stream and
returns once `SessionAccepted` is received. One session at a time: `RestartAsync(session)` when the camera list
changes (the worker starts from scratch), `StopAsync()` to stop.

| Field | Role |
|---|---|
| `AiCamera.Role` | Domain hint (podcast: `host`, `guest`, `wide`, `host_zoom`...). Empty: the worker decides (labels). |
| `AiCamera.AudioChannel` | Microphone of the camera in `WriteAudio`, `-1` = none. |
| `Mode` | `Suggest`: the operator decides (Studio "agent" mode). `Auto`: the SDK arbiter cuts (Studio "auto" mode). |
| `Arbiter` (<xref:Castor.AI.ArbiterOptions>) | Auto mode: `MinConfidence` 0.5, `MaxDecisionAgeMs` 500, `ManualLockMs` 5000, `DefaultMinHoldMs` 1500. |
| `Parameters` | Domain tuning, forwarded as is in `SessionStart.params`. |

## 5. Send to the worker

All these methods are thread-safe and non-blocking; without a session they do nothing (or return `false`).

### What is on air

```csharp
// On every cut made by the operator, or suggestion applied in Suggest mode.
_ai.ReportOnAir(scene.Id.ToString("N"));
```

**Required**: the worker reasons about the shot really on air (`CameraOnAir`). In Auto mode, `ReportOnAir`
also locks AI cuts for `ManualLockMs`. Cuts made by the arbiter are already reported to the worker by the SDK.

### Audio (podcast)

```csharp
// Audio callback of an OBS source (obs_source_add_audio_capture_callback), any thread.
_ai.WriteAudio(channel, monoSamples, sampleRate);   // float32 [-1, 1], 48 kHz is fine: the SDK resamples to 16 kHz
```

One channel per microphone, tied to its camera by `AiCamera.AudioChannel`. The SDK paces the audio in 32 ms
blocks, writes it to the shared ring and sends `AudioReady`.

### Video (football)

```csharp
// Raw BGRA render of a scene, one producer per camera.
_ai.WriteVideo(scene.Id.ToString("N"), bgra, width, height, stride);
```

Any size: the SDK scales to `_ai.VideoFormat` (640x360 BGRA8, 15 fps for football) and drops frames that come
too early. Producing frames at that size avoids the scaling cost. `WriteVideo` returns `false` when the frame
is dropped; it has no effect for a worker without video (podcast).

### Payloads

```csharp
_ai.SendPayload(PayloadTypes.CameraMetadata, cameraId, new Dictionary<string, object?>
{
    ["face_detected"] = true,
    ["volume_db"] = -18.5,
});
_ai.SendPayload(PayloadTypes.SpeakerStart, cameraId);
```

Accepted types: `Capabilities.PayloadTypes` (constants in <xref:Castor.AI.Contracts.PayloadTypes>).
Values: `null`, `bool`, `string` or a number; `camera_id` is added when `cameraId` is given.

## 6. Handle suggestions

```csharp
private void OnSuggestion(AiSuggestion suggestion)
{
    if (suggestion.Verdict is null)
    {
        // Suggest: offer it to the operator; if accepted, the operator cuts, then the app calls ReportOnAir.
        ShowSuggestion(suggestion.CameraId, suggestion.Confidence, suggestion.Reason);
    }
    else if (!suggestion.Verdict.Accepted)
    {
        // Auto: rejected by the arbiter (stale, confidence, min_hold, manual lock), debug only.
        AddLog("arbiter", $"{suggestion.CameraId}: {suggestion.Verdict.Reason}");
    }
}

private void OnAirChanged(AiOnAirChange change)
{
    if (change.Source == AiOnAirSource.Ai)
    {
        // Auto: the arbiter accepted, Studio switches. No ReportOnAir here, the SDK already reported it.
        _workspace.SelectScene(SceneById(Guid.ParseExact(change.CameraId, "N")));
    }
}
```

`AiSuggestion.LatencyMs` (reception − `SourcePtsUs`) and `ProcessingMs` are used to track latency
(target < 300 ms locally).

## Lifecycle and failures

```
Stopped ─StartAsync─► Starting ──SessionAccepted──► Running ──StopAsync──► Stopped
                         │                             │
                    failure: StartAsync           worker crash, stream broken
                    throws, Faulted                    ▼
                                                    Faulted ─► Restarting (1 s, 2 s, 5 s...) ─► Running
```

- **Crash**: `LocalWorkerProvider` starts a fresh worker and replays the same session. `SessionStarted` is raised
  again and the SDK tells the new worker what is on air. The app has nothing to do, except show the state and
  stay manual while it is not `Running`.
- **Long startup**: while models download, `StateChanged` reports `Starting` with the progress
  ("downloading football/checkpoint_5_416.onnx 42 %"): show it in the AI UI.
- **App shutdown**: `StopAsync()` (or `DisposeAsync()`) sends `SessionStop` and releases the regions. If the app
  dies without doing it, the worker stops on its own (`--parent-pid`).

## SDK ↔ gRPC contract mapping

| App side (`AiClient`) | `Session` stream message |
|---|---|
| `StartAsync` | `Health` until `READY`, `GetCapabilities`, then `ClientMessage.start` (`SessionStart`) |
| `ReportOnAir`, arbiter cut | `ClientMessage.on_air` (`CameraOnAir`, `manual` true / false) |
| `WriteAudio` | shared audio ring + `ClientMessage.audio` (`AudioReady`) |
| `WriteVideo` | camera video region + `ClientMessage.video_frame` (`VideoFrameReady`) |
| `SendPayload` | `ClientMessage.payload` (`PayloadEvent`) |
| `StopAsync` | `ClientMessage.stop` (`SessionStop`) |
| `SuggestionReceived` | `WorkerMessage.decision` (`SceneDecision`) |
| `StatsReceived` | `WorkerMessage.stats` (`WorkerStats`) |
| `LogReceived` | `WorkerMessage.log`, `error`, `ended` |
| `SessionStarted` | `WorkerMessage.accepted` (`SessionAccepted`) |

The full sequence diagram is in [Architecture, section 2](../architecture.md#2-session-lifecycle-local).

## Deployment

Studio ships the worker in `ai/` next to the app. Every `v*.*.*` tag of this repository publishes
`Castor.AI.Worker-<version>-win-x64.zip` (self-contained, no models) and its `.sha256` as assets of the GitHub
release (`.github/workflows/worker-release.yml`). **Always take the worker of the `Castor.AI` version
referenced by Studio**: they share `castor_ai.proto` and the shared memory layouts. Download and verification
script for the installer: [SDK README, "Deployment"](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md#deployment).

Models are not in the installer: on first launch, the worker downloads the ones of its domain.
