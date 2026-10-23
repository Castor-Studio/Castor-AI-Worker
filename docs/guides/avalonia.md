# Communiquer depuis l'app Avalonia

L'app Avalonia (Castor Studio, ou la console de debug `Castor.AI.Dashboard`) ne parle **jamais** directement
au worker : ni gRPC, ni mémoire partagée, ni process. Tout passe par <xref:Castor.AI.AiClient>, la façade du
SDK `Castor.AI`, qui n'a aucune dépendance UI.

```
Vue Avalonia ─ binding ─ ViewModel
                           │ StartAsync, ReportOnAir, WriteAudio, WriteVideo, SendPayload
                           ▼
                        AiClient ── horloge de session, arbitre (mode Auto), AudioFeed, VideoFeed
                           │ IAiProvider
                           ▼
                        LocalWorkerProvider ── lance Castor.AI.Worker.exe, supervise, redémarre
                           │ gRPC (named pipe)              │ mémoire partagée
                           ▼                                ▼
                        AiWorker.Session              régions vidéo + ring audio
                           │
                           ▼
                        Castor.AI.Worker
                           │ SceneDecision, WorkerStats, WorkerLog
                           ▼
                        événements AiClient ── Dispatch ──► thread UI Avalonia
```

Référence complète : `Castor.AI.Dashboard/ViewModels/MainViewModel.cs`, qui n'utilise l'IA qu'à travers ce SDK,
exactement comme Castor Studio.

## 1. Référencer le SDK

Castor Studio référence le paquet `Castor.AI` (GitHub Packages `Castor-Studio`, `Castor.AI.Contracts` suit en
dépendance) ; la source NuGet et le jeton sont décrits dans le
[README du SDK](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md#paquet-nuget).

```xml
<PackageReference Include="Castor.AI" Version="1.0.0" />
```

Dans ce repo, `Castor.AI.Dashboard` référence directement les projets.

## 2. Enregistrer `AiClient` et le thread UI

Les événements du SDK partent de ses propres threads (lecture du stream gRPC, supervision). Une app Avalonia
ne peut toucher ses contrôles et ses propriétés bindées que depuis le thread UI : `AiClientOptions.Dispatch`
redirige **tous** les événements vers lui.

Avec l'injection de dépendances (Castor Studio) :

```csharp
services.AddCastorAI(options =>
{
    options.Worker = new LocalWorkerOptions
    {
        // Worker livré dans ai/ à côté de l'app (voir « Déploiement »).
        WorkerPath = Path.Combine(AppContext.BaseDirectory, "ai", "Castor.AI.Worker.exe"),
    };
    options.Client = new AiClientOptions { Dispatch = action => Dispatcher.UIThread.Post(action) };
});
```

`AddCastorAI` enregistre `LocalWorkerOptions`, `IAiProvider` (<xref:Castor.AI.Providers.Local.LocalWorkerProvider>)
et `AiClient` en singletons : injecter `AiClient` dans le ViewModel.

Sans conteneur (`Castor.AI.Dashboard`) :

```csharp
_ai = new AiClient(new LocalWorkerProvider(), new AiClientOptions { Dispatch = Ui });

private static void Ui(Action action)
{
    if (Dispatcher.UIThread.CheckAccess()) action();
    else Dispatcher.UIThread.Post(action);
}
```

Sans `WorkerPath`, le SDK cherche le worker dans `CASTOR_WORKER_PATH`, puis à côté de l'app, puis dans la sortie
du projet `Castor.AI.Worker` (layout de dev `bin/<config>/<tfm>/`).

<xref:Castor.AI.Providers.Local.LocalWorkerOptions> :

| Option | Défaut | Rôle |
|---|---|---|
| `WorkerPath` | recherche ci-dessus | Chemin de `Castor.AI.Worker.exe`. |
| `ReadyTimeout` | 10 s | Attente de `Health = READY`, comptée depuis la dernière progression (un long téléchargement de modèle n'expire pas). |
| `RestartBackoff` | 1 s, 2 s, 5 s | Délais avant de rouvrir une session après un crash ; le dernier se répète. |
| `AudioRingSeconds` | 2 | Taille du ring audio en mémoire partagée. |

## 3. S'abonner aux événements

À faire une fois, avant `StartAsync`. Avec `Dispatch`, les handlers tournent sur le thread UI.

```csharp
_ai.StateChanged += (_, change) => AiStatusText = $"{change.State}: {change.Detail}";
_ai.SessionStarted += (_, capabilities) => BuildCameraGauges(capabilities.Metrics, capabilities.CameraFlags);
_ai.SuggestionReceived += (_, suggestion) => OnSuggestion(suggestion);
_ai.OnAirChanged += (_, change) => OnAirChanged(change);
_ai.StatsReceived += (_, stats) => UpdateGauges(stats);
_ai.LogReceived += (_, entry) => AddLog(entry.Source, entry.Message);
```

| Événement | Message worker d'origine | Usage dans l'app |
|---|---|---|
| `StateChanged` (<xref:Castor.AI.AiStateChange>) | `Health`, process, stream | Statut IA : `Starting` (avec la progression du téléchargement des modèles), `Running`, `Faulted`, `Restarting`, `Stopped`. |
| `SessionStarted` (`WorkerCapabilities`) | `SessionAccepted` | Adapter l'UI : métriques, flags, payloads acceptés, `VideoFormat`. Relevé aussi après chaque redémarrage. |
| `SuggestionReceived` (<xref:Castor.AI.AiSuggestion>) | `SceneDecision` | Mode Suggest : afficher caméra, confiance, raison. Mode Auto : `Verdict` dit si l'arbitre l'a prise. |
| `OnAirChanged` (<xref:Castor.AI.AiOnAirChange>) | arbitre du SDK | `Source == Ai` : **l'app doit basculer** sur `CameraId`. `Initial`, `Manual` : information. |
| `StatsReceived` (`WorkerStats`) | `WorkerStats` (toutes les 250 ms) | Jauges et graphes, clés décrites par `Capabilities.Metrics` et `CameraFlags`. |
| `LogReceived` (<xref:Castor.AI.AiLogEntry>) | `WorkerLog`, `SessionError`, `SessionEnded`, stdout du worker | Panneau de logs. `Source` : `worker`, `local`, `audio` ou `client`. |

## 4. Démarrer une session

Une caméra IA = une scène de Studio. L'`Id` est libre (ici le `Guid` de la scène) et revient tel quel dans les
suggestions.

```csharp
var session = new AiSessionOptions
{
    Domain = "football",                                   // ou "podcast"
    Mode = AiDecisionMode.Suggest,                          // ou Auto
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
    // Worker introuvable, pas READY à temps, ou session refusée (SessionError) : rester en manuel.
    AiStatusText = ex.Message;
}
```

`StartAsync` lance le worker, attend `READY`, crée les régions de mémoire partagée, ouvre le stream et rend la
main une fois `SessionAccepted` reçu. Une seule session à la fois : `RestartAsync(session)` quand la liste des
caméras change (le worker repart de zéro), `StopAsync()` pour arrêter.

| Champ | Rôle |
|---|---|
| `AiCamera.Role` | Indice de domaine (podcast : `host`, `guest`, `wide`, `host_zoom`...). Vide : le worker décide (labels). |
| `AiCamera.AudioChannel` | Micro de la caméra dans `WriteAudio`, `-1` = aucun. |
| `Mode` | `Suggest` : l'opérateur décide (mode « agent » de Studio). `Auto` : l'arbitre du SDK coupe (mode « auto »). |
| `Arbiter` (<xref:Castor.AI.ArbiterOptions>) | Mode Auto : `MinConfidence` 0,5, `MaxDecisionAgeMs` 500, `ManualLockMs` 5 000, `DefaultMinHoldMs` 1 500. |
| `Parameters` | Réglages du domaine, transmis tels quels dans `SessionStart.params`. |

## 5. Envoyer au worker

Toutes ces méthodes sont thread-safe et non bloquantes ; sans session, elles ne font rien (ou renvoient `false`).

### Ce qui est à l'antenne

```csharp
// À chaque coupe faite par l'opérateur, ou suggestion appliquée en mode Suggest.
_ai.ReportOnAir(scene.Id.ToString("N"));
```

**Obligatoire** : le worker raisonne sur le plan réellement à l'antenne (`CameraOnAir`). En mode Auto, un
`ReportOnAir` verrouille aussi les coupes IA pendant `ManualLockMs`. Les coupes de l'arbitre sont déjà
signalées au worker par le SDK.

### Audio (podcast)

```csharp
// Callback audio d'une source OBS (obs_source_add_audio_capture_callback), n'importe quel thread.
_ai.WriteAudio(channel, monoSamples, sampleRate);   // float32 [-1, 1], 48 kHz OK : le SDK rééchantillonne en 16 kHz
```

Un canal par micro, relié à sa caméra par `AiCamera.AudioChannel`. Le SDK cadence l'audio en blocs de 32 ms,
l'écrit dans le ring partagé et envoie `AudioReady`.

### Vidéo (football)

```csharp
// Rendu brut BGRA d'une scène, un producteur par caméra.
_ai.WriteVideo(scene.Id.ToString("N"), bgra, width, height, stride);
```

N'importe quelle taille : le SDK met à l'échelle vers `_ai.VideoFormat` (640x360 BGRA8, 15 fps pour le
football) et jette les frames en avance. Produire directement à cette taille évite le coût de mise à l'échelle.
`WriteVideo` renvoie `false` si la frame est jetée ; sans effet pour un worker sans vidéo (podcast).

### Payloads

```csharp
_ai.SendPayload(PayloadTypes.CameraMetadata, cameraId, new Dictionary<string, object?>
{
    ["face_detected"] = true,
    ["volume_db"] = -18.5,
});
_ai.SendPayload(PayloadTypes.SpeakerStart, cameraId);
```

Types acceptés : `Capabilities.PayloadTypes` (constantes dans <xref:Castor.AI.Contracts.PayloadTypes>).
Valeurs : `null`, `bool`, `string` ou nombre ; `camera_id` est ajouté quand `cameraId` est donné.

## 6. Réagir aux suggestions

```csharp
private void OnSuggestion(AiSuggestion suggestion)
{
    if (suggestion.Verdict is null)
    {
        // Suggest : proposer à l'opérateur ; s'il accepte, il coupe puis appelle ReportOnAir.
        ShowSuggestion(suggestion.CameraId, suggestion.Confidence, suggestion.Reason);
    }
    else if (!suggestion.Verdict.Accepted)
    {
        // Auto : refusée par l'arbitre (périmée, confiance, min_hold, verrou manuel), debug seulement.
        AddLog("arbiter", $"{suggestion.CameraId}: {suggestion.Verdict.Reason}");
    }
}

private void OnAirChanged(AiOnAirChange change)
{
    if (change.Source == AiOnAirSource.Ai)
    {
        // Auto : l'arbitre a accepté, Studio bascule. Pas de ReportOnAir ici, le SDK l'a déjà signalé.
        _workspace.SelectScene(SceneById(Guid.ParseExact(change.CameraId, "N")));
    }
}
```

`AiSuggestion.LatencyMs` (réception − `SourcePtsUs`) et `ProcessingMs` servent au suivi de la latence
(cible < 300 ms en local).

## Cycle de vie et pannes

```
Stopped ─StartAsync─► Starting ──SessionAccepted──► Running ──StopAsync──► Stopped
                         │                             │
                    échec : exception             crash du worker, stream coupé
                    de StartAsync, Faulted             ▼
                                                    Faulted ─► Restarting (1 s, 2 s, 5 s...) ─► Running
```

- **Crash** : `LocalWorkerProvider` relance un worker neuf et rejoue la même session. `SessionStarted` est relevé
  à nouveau et le SDK renvoie au nouveau worker le plan à l'antenne. L'app n'a rien à faire, sinon afficher l'état
  et rester en manuel tant qu'il n'est pas `Running`.
- **Démarrage long** : pendant le téléchargement des modèles, `StateChanged` remonte `Starting` avec la
  progression (« downloading football/checkpoint_5_416.onnx 42 % ») : à afficher dans l'UI IA.
- **Fermeture de l'app** : `StopAsync()` (ou `DisposeAsync()`) envoie `SessionStop` et libère les régions. Si l'app
  meurt sans le faire, le worker s'arrête seul (`--parent-pid`).

## Correspondance SDK ↔ contrat gRPC

| Côté app (`AiClient`) | Message du stream `Session` |
|---|---|
| `StartAsync` | `Health` jusqu'à `READY`, `GetCapabilities`, puis `ClientMessage.start` (`SessionStart`) |
| `ReportOnAir`, coupe de l'arbitre | `ClientMessage.on_air` (`CameraOnAir`, `manual` vrai / faux) |
| `WriteAudio` | ring audio partagé + `ClientMessage.audio` (`AudioReady`) |
| `WriteVideo` | région vidéo de la caméra + `ClientMessage.video_frame` (`VideoFrameReady`) |
| `SendPayload` | `ClientMessage.payload` (`PayloadEvent`) |
| `StopAsync` | `ClientMessage.stop` (`SessionStop`) |
| `SuggestionReceived` | `WorkerMessage.decision` (`SceneDecision`) |
| `StatsReceived` | `WorkerMessage.stats` (`WorkerStats`) |
| `LogReceived` | `WorkerMessage.log`, `error`, `ended` |
| `SessionStarted` | `WorkerMessage.accepted` (`SessionAccepted`) |

Le diagramme de séquence complet est dans [Architecture, section 2](../architecture.md#2-cycle-de-vie-dune-session-local).

## Déploiement

Studio livre le worker dans `ai/` à côté de l'app. Chaque tag `v*.*.*` de ce repo publie
`Castor.AI.Worker-<version>-win-x64.zip` (self-contained, sans modèle) et son `.sha256` en assets de la release
GitHub (`.github/workflows/worker-release.yml`). **Toujours prendre le worker de la version de `Castor.AI`
référencée par Studio** : ils partagent `castor_ai.proto` et les formats de mémoire partagée. Script de
téléchargement et de vérification pour l'installeur : [README du SDK, « Déploiement »](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md#déploiement).

Les modèles ne sont pas dans l'installeur : le worker télécharge au premier lancement ceux de son domaine.
