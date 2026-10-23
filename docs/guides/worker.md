# How the worker works

`Castor.AI.Worker` is a .NET 10 console process (ASP.NET Core + ONNX Runtime). One process serves **a single
domain** (`podcast`, `football`), chosen at launch. It exposes the `AiWorker` gRPC service on a named pipe,
reads media from shared memory and sends back shot suggestions (`SceneDecision`).

The worker never decides on its own what goes on air: it **suggests**. The app stays the final authority
(SDK arbiter or operator) and tells it what is really on air (`CameraOnAir`).

The app does not start the worker itself: `LocalWorkerProvider` (`Castor.AI` SDK) does. See
[Talking to the worker from the Avalonia app](avalonia.md).

## Launch

```
Castor.AI.Worker.exe --domain podcast|football --pipe <name> [--parent-pid <pid>]
```

| Argument | Default | Role |
|---|---|---|
| `--domain` | `podcast` | Domain served by this process. Unknown: immediate exit with code 2. |
| `--pipe` | `castor-ai-worker` | Name of the named pipe to listen on. The SDK picks a unique one per session: `castor-ai-<guid>`. |
| `--parent-pid` | none | PID of the app. The worker stops when that process goes away (`ParentWatchdog`). |

Exit codes:

| Code | Cause |
|---|---|
| 0 | Normal shutdown (parent gone, stop requested). |
| 2 | Unknown domain. Checked before touching the models or the pipe (used as the smoke test in `worker-release.yml`). |
| 3 | Model not found, download or checksum failed (`Warmup.ModelLoadFailedExitCode`). |

Environment variables read by the worker (details in [Architecture, section 9](../architecture.md#9-modèles--installation-et-téléchargement)):

| Variable | Role |
|---|---|
| `CASTOR_PACKS_DIR` | Folder of installed model packs. |
| `CASTOR_MODELS_DIR` | Cache of downloaded models (default `%LOCALAPPDATA%\Castor\models`). |
| `CASTOR_MODELS_URL` | Download mirror: `<url>/<file>`. |
| `CASTOR_MODELS_MANIFEST` | Replaces the embedded `models.json`, without a rebuild. |

The worker's `stdout` and `stderr` are read by the SDK and surfaced in `AiClient.LogReceived`
(`[worker stdout] ...`). ASP.NET Core logs are filtered at the `Warning` level.

Start a worker by hand, to debug it without an app:

```powershell
dotnet run --project Castor.AI.Worker -- --domain football --pipe castor-debug
```

## Startup

```
Program.cs
  ├ WorkerOptions.Parse(args)           --domain, --pipe, --parent-pid
  ├ Kestrel ListenNamedPipe(HTTP/2)     AiWorker gRPC service
  ├ Warmup (BackgroundService)          domain models, then Health = READY
  └ ParentWatchdog (BackgroundService)  shutdown when the app dies
```

1. Kestrel listens on the pipe right away: `Health` answers while models load.
2. `Warmup` takes each model of `StrategyCatalog.RequiredModels`: installed pack, else cache, else a download
   checked against its SHA-256 (`ModelProvisioner`). Progress goes to `Health.detail`
   ("downloading football/checkpoint_5_416.onnx 42 %"), then the models are loaded into ONNX Runtime
   (`ModelStore`).
3. `WorkerState.Status` goes from `STATUS_STARTING` to `STATUS_READY`. Before that, every session is refused
   (`CODE_INTERNAL`, "Worker not ready").

Models are loaded **once per process** and shared between sessions; each session creates its own strategy
(VAD states, focus...).

## gRPC service

Contract: `Castor.AI.Contracts/Protos/castor_ai.proto` (package `castor.ai.v1`), implemented by
`AiWorkerService`.

| RPC | Response |
|---|---|
| `Health` | `status` (`STARTING`, `READY`), `active_sessions`, `detail` (startup progress). Polled by the SDK until `READY`. |
| `GetCapabilities` | `WorkerCapabilities` of the domain: required inputs, `max_cameras`, `preferred_video`, `preferred_audio`, `payload_types`, metrics and flags published in the stats. |
| `Session` | Bidirectional stream, one per session. The first client message must be `SessionStart`. |

`SessionStart` validation. Each refusal is a `SessionError` with `fatal = true`, then the stream closes:

| Condition | Code |
|---|---|
| First message other than `start` | `CODE_BAD_FORMAT` |
| Worker not `READY` yet | `CODE_INTERNAL` |
| `domain` differs from the process domain | `CODE_UNSUPPORTED_DOMAIN` |
| No camera | `CODE_BAD_FORMAT` |
| More cameras than `max_cameras` | `CODE_TOO_MANY_CAMERAS` |
| Shared memory regions cannot be opened | `CODE_TRANSPORT_FAILED` |
| The strategy rejects the session (invalid parameter, video format...) | `CODE_BAD_FORMAT` |

Otherwise the worker answers `SessionAccepted` (with its capabilities) and a startup `WorkerLog`.

## Session flow

`SessionRunner` runs the session on decoupled loops, so a slow model never blocks reception:

```
app stream ──► read loop ──► strategy (OnPayload, OnAir) ──► wake-up
                                                               │
            inference loop ◄── wake-up (capacity 1, latest-wins) or 100 ms tick
                 │ strategy.Evaluate(now) ─► SceneDecision ?
                 ▼
            outbox ◄── stats loop (every 250 ms): WorkerStats
                 │
            write loop: only writer of the stream (gRPC writes are not concurrent-safe)
```

| Received message (`ClientMessage`) | Effect in the worker |
|---|---|
| `payload` (`PayloadEvent`) | `strategy.OnPayload`, then wakes inference. |
| `on_air` (`CameraOnAir`) | `strategy.OnAir`: the shot really on air (AI or manual). |
| `video_frame` (`VideoFrameReady`) | Wake-up: the strategy reads the **latest** frame of the region (latest-wins). |
| `audio` (`AudioReady`) | Wake-up: the VAD consumes the audio ring without waiting for the tick. |
| `stop` (`SessionStop`) | Ends the read loop, `SessionEnded` with the reason. |
| duplicate `start` | Ignored, warning `WorkerLog`. |

| Sent message (`WorkerMessage`) | When |
|---|---|
| `accepted` | Once, after validation and `strategy.Start`. |
| `decision` (`SceneDecision`) | When `Evaluate` has a new suggestion; `processing_ms` = compute time. |
| `stats` (`WorkerStats`) | Every 250 ms: `inference_ms_avg`, then metrics and flags filled by the strategy. |
| `log` (`WorkerLog`) | Session start, warnings. |
| `error` (`SessionError`) | Session refused (see above). |
| `ended` (`SessionEnded`) | Always, at the end of the session: stop requested, stream closed or app disconnected. |

**Clock.** The worker has no clock of its own: its `SessionClock` follows the highest `pts_us` received from
the app (microseconds since `SessionStart`). Every decision carries `source_pts_us`, the pts of the newest
input used: the app derives the latency from it and drops stale decisions.

**Latest-wins.** Several inputs between two evaluations give a single wake-up; the strategy always reads the
newest frame. No queue, so delay never builds up. The region layout and the read algorithm are described in
[Architecture, section 4](../architecture.md#4-format-shared-memory).

**Refused decision.** A suggestion that is not followed (the app sends another `CameraOnAir`) is suggested
again after 1 s; the worker always reasons about the shot the app really took.

## Per-domain strategies

The business logic of a domain implements `IDomainStrategy`. `SessionRunner` calls it under a lock, never
concurrently:

| Method | Role |
|---|---|
| `Start(SessionStart, SessionMedia, log)` | Reads cameras, roles and `params`; throws to refuse the session. |
| `OnPayload(PayloadEvent)` | Hints from the app (`speaker.start`, `camera.metadata`...). |
| `OnAir(CameraOnAir)` | Shot really on air. |
| `Evaluate(nowUs)` | Consumes pending media, returns a `SceneDecision` or `null`. |
| `FillStats(WorkerStats)` | Metrics and flags declared in the capabilities. |

| Domain | Inputs | Model | Details |
|---|---|---|---|
| `podcast` | audio (1 channel per microphone), payloads | Silero VAD | [Architecture, section 7](../architecture.md#7-worker-podcast) |
| `football` | video 640x360 BGRA8 at 15 fps, 4 cameras max | YOLO (ball) | [Architecture, section 8](../architecture.md#8-worker-football) |

Adding a domain:

1. a strategy in `Strategies/<Domain>/` implementing `IDomainStrategy`, with a `Describe(workerId)` that
   returns its `WorkerCapabilities`;
2. register it in `StrategyCatalog` (`KnownDomains`, `Capabilities`, `RequiredModels`, `LoadModels`,
   `Create`);
3. its models in `Models/models.json` (pinned SHA-256), published by `scripts/publish-models.ps1`;
4. a `profiles/<domain>.json` profile to test it in `Castor.AI.Dashboard`.

The app has nothing to change: it adapts to the capabilities (max cameras, video and audio format, payloads,
metrics).

## Failures

- **App gone**: `ParentWatchdog` stops the worker, which never keeps the GPU or the pipe as an orphan.
- **Stream closed by the app**: the session ends cleanly, the regions are unmapped; the process stays ready
  for another session.
- **Worker crash**: seen by the SDK (process exited or stream broken), which starts a fresh worker with backoff
  (1 s, 2 s, 5 s) and replays the session. See [Lifecycle and failures](avalonia.md#lifecycle-and-failures).
- **Model unavailable**: exit code 3 and an explicit message in the logs, surfaced by the SDK.
