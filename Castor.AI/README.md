# Castor.AI

Client SDK of the Castor Studio directing AI: it starts an AI worker, sends it the cameras, the audio and the
app hints, and surfaces the suggested shots. No UI dependency (no Avalonia).

```
Castor.AI            ← referenced by the app (Castor Studio, Castor.AI.Dashboard)
  └ Castor.AI.Contracts   gRPC contract + shared memory layout (shared with the workers)
Castor.AI.Worker     ← executable started by the SDK (one domain per process: podcast, ...)
```

## API at a glance

| Type | Role |
|---|---|
| `AiClient` | Entry point. One session at a time. Thread-safe. |
| `AiSessionOptions` | Domain, cameras, domain parameters, mode, arbiter rules. |
| `AiCamera(Id, Label, Role, AudioChannel)` | A camera (a scene in Studio). Free-form `Id`, echoed back as is in the suggestions. |
| `AiDecisionMode.Suggest` / `Auto` | Suggest: the app shows, the operator decides ("agent" mode). Auto: the SDK arbiter cuts ("auto" mode). |
| `AiSuggestion` | Suggested shot: camera, confidence, reason, latency, scores, arbiter verdict (Auto). |
| `AiOnAirChange` | What is on air changed: `Initial`, `Ai` (Auto, the app must switch), `Manual`. |
| `LocalWorkerProvider` | The worker runs locally (child process, gRPC over named pipe, audio and video in shared memory). |

`AiClient` methods:

- `StartAsync(session)`, `StopAsync()`, `RestartAsync(session)` (camera change: the worker starts from scratch);
- `ReportOnAir(cameraId)`: **call it on every cut made by the app or the operator** (the worker reasons about the current shot; in Auto, it locks AI cuts for `ManualLockMs`);
- `WriteAudio(channel, samples, sampleRate)`: audio of one microphone, mono float32, any rate, any thread (resampled to 16 kHz and paced by the SDK);
- `WriteVideo(cameraId, bgra, width, height, stride)`: frame of one camera, BGRA8, any size, any thread (one producer per camera); scaled to `VideoFormat` and limited to its fps by the SDK. No effect if the worker reads no video (podcast);
- `SendPayload(type, cameraId, fields)`: domain-specific hints (types listed in `Capabilities.PayloadTypes`).

Events: `SuggestionReceived`, `OnAirChanged`, `StateChanged` (including automatic restart after a worker crash),
`SessionStarted`, `StatsReceived` (metrics described by `Capabilities.Metrics`), `LogReceived`.
They are raised through `AiClientOptions.Dispatch` when provided (otherwise on an SDK thread).

## NuGet package

`Castor.AI` and `Castor.AI.Contracts` are published to GitHub Packages (`Castor-Studio`) by
`.github/workflows/nuget-publish.yml` on every `v*.*.*` tag (package version = tag without the `v`):

```bash
git tag v1.0.0
git push origin v1.0.0
```

The same tag also publishes the worker executable as an asset of the tag's GitHub release
(`.github/workflows/worker-release.yml`, see "Deployment").

On PRs to `main` / `dev`, `.github/workflows/ci.yml` only tests and packs (packages as an artifact, version `0.0.0-ci.<run>`).

On the Castor Studio side, add the source in `nuget.config`:

```xml
<packageSources>
  <add key="castor-github" value="https://nuget.pkg.github.com/Castor-Studio/index.json" />
</packageSources>
<packageSourceCredentials>
  <castor-github>
    <add key="Username" value="%GITHUB_ACTOR%" />
    <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
  </castor-github>
</packageSourceCredentials>
```

`GITHUB_TOKEN`: locally a PAT with `read:packages`, in CI the workflow `GITHUB_TOKEN`
(`permissions: packages: read`). Then reference only `Castor.AI` (`Castor.AI.Contracts` comes as a dependency):

```xml
<PackageReference Include="Castor.AI" Version="1.0.0" />
```

## Castor Studio integration

### 1. Registration (`ServiceCollectionExtensions.AddCommonServices`)

```csharp
services.AddCastorAI(options =>
{
    options.Worker = new LocalWorkerOptions
    {
        // Worker + model packs/ shipped next to the app (see "Deployment").
        WorkerPath = Path.Combine(AppContext.BaseDirectory, "ai", "Castor.AI.Worker.exe"),
    };
    options.Client = new AiClientOptions { Dispatch = action => Dispatcher.UIThread.Post(action) };
});
```

`AiClient` is then an injectable singleton (replaces `IAiAnalysisClient` / `GrpcAiAnalysisClient`).

### 2. Session (`MulticamViewModel.StartAiAsync`)

```csharp
var session = new AiSessionOptions
{
    Domain = GetSelectedModuleName(),                     // "podcast", "football"
    Mode = mode == "auto" ? AiDecisionMode.Auto : AiDecisionMode.Suggest,
    Cameras = selectedScenes
        .Select(scene => new AiCamera(scene.Id.ToString("N"), scene.Name /*, role, microphone */))
        .ToList(),
    InitialCameraId = _workspace.SelectedScene?.Id.ToString("N"),
};
await _ai.StartAsync(session);
```

### 3. React

```csharp
_ai.SuggestionReceived += (_, suggestion) =>
{
    // Agent mode: show the suggestion (scene, confidence, reason).
    if (suggestion.Verdict is null) ShowSuggestion(suggestion.CameraId, suggestion.Confidence);
};

_ai.OnAirChanged += (_, change) =>
{
    // Auto mode: the arbiter accepted, Studio switches.
    if (change.Source == AiOnAirSource.Ai) _workspace.SelectScene(SceneById(Guid.ParseExact(change.CameraId, "N")));
};

_ai.StateChanged += (_, change) => AiStatusText = $"{change.State}: {change.Detail}";
```

And on every scene change made by the operator (or suggestion applied in agent mode):

```csharp
_ai.ReportOnAir(scene.Id.ToString("N"));
```

### 4. Audio

One channel per microphone. `AiCamera.AudioChannel` ties the camera to its microphone. Studio hooks the audio
callback of each relevant OBS source (`obs_source_add_audio_capture_callback`):

```csharp
_ai.WriteAudio(channel, monoSamples, sampleRate);   // OBS 48 kHz → 16 kHz done by the SDK
```

Without audio (`AudioChannel = -1` everywhere), the podcast worker relies on the `speaker.*` / `camera.metadata` payloads only.

### 5. Video (football)

The football worker reads one frame per camera. `AiClient.VideoFormat` gives the wanted size (640x360 BGRA, 15 fps):
producing frames directly at that size avoids the SDK scaling.

```csharp
// For each scene of the session, from a raw OBS video callback (BGRA):
_ai.WriteVideo(scene.Id.ToString("N"), bgra, width, height, linesize);
```

Today Studio pushes one encoded stream per scene to a URL (`LibObsAiSceneStreamRuntime`): locally it needs
a raw render per scene instead (independent video output + `obs_add_raw_video_callback` or equivalent).

### Deployment

The SDK starts `Castor.AI.Worker.exe`. Studio ships the worker in an `ai/` folder next to the app
(`LocalWorkerOptions.WorkerPath`, see the registration above).

Every `v*.*.*` tag of this repository creates a GitHub release with two assets (`.github/workflows/worker-release.yml`):

| Asset | Content |
|---|---|
| `Castor.AI.Worker-<version>-win-x64.zip` | `dotnet publish -r win-x64 --self-contained`: `Castor.AI.Worker.exe`, .NET runtime and ONNX Runtime native DLLs. No runtime to install on the machine. No models. |
| `Castor.AI.Worker-<version>-win-x64.zip.sha256` | `<sha256>  <file>` (`sha256sum` format). |

`<version>` = tag without the `v`, the same as the tag's `Castor.AI` package. **Always take the worker of the
`Castor.AI` version referenced by Studio**: they share `castor_ai.proto` and the shared memory layouts.

In the Studio build / installer pipeline (PowerShell, version read from the `Castor.AI` `PackageReference`):

```powershell
$version = "1.0.0"
$asset = "Castor.AI.Worker-$version-win-x64.zip"
gh release download "v$version" --repo Castor-Studio/Castor-AI-Worker --pattern "$asset*" --dir build/ai
$expected = (Get-Content "build/ai/$asset.sha256").Split(" ")[0]
$actual = (Get-FileHash "build/ai/$asset" -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "${asset}: SHA-256 $actual, expected $expected" }
Expand-Archive "build/ai/$asset" -DestinationPath <app output>/ai -Force
```

Without `gh`, the direct URL is `https://github.com/Castor-Studio/Castor-AI-Worker/releases/download/v<version>/<asset>`
(private repository: token with read access to the repository, `gh` handles it).

No models in the installer: at startup, the worker downloads the missing ones of its domain
(see `docs/architecture.md`, section 9). During the download, `StateChanged` reports
`Starting` with the progress ("downloading football/checkpoint_5_416.onnx 42 % ..."): show it in the AI UI.
`LocalWorkerOptions.ReadyTimeout` counts from the last progress report, so a long download does not time out.

The worker stops on its own if Studio dies (parent PID watchdog).

## Left to do on the Studio side

- UI to pick the microphone per scene (`AudioChannel`) and, for the podcast, the role (`Role`);
- OBS audio capture per microphone to `WriteAudio`;
- raw render per scene to `WriteVideo` (football);
- download of the worker zip (version of `Castor.AI`) in the installer, see "Deployment".
