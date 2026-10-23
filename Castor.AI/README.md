# Castor.AI

SDK client de l'IA de réalisation pour Castor Studio : il démarre un worker IA, lui envoie les caméras,
l'audio et les indications de l'app, et remonte les plans suggérés. Aucune dépendance UI (pas d'Avalonia).

```
Castor.AI            ← référencé par l'app (Castor Studio, Castor.AI.Dashboard)
  └ Castor.AI.Contracts   contrat gRPC + format mémoire partagée (partagé avec les workers)
Castor.AI.Worker     ← exécutable lancé par le SDK (un domaine par process : podcast, ...)
```

## API en bref

| Type | Rôle |
|---|---|
| `AiClient` | Point d'entrée. Une session à la fois. Thread-safe. |
| `AiSessionOptions` | Domaine, caméras, paramètres du domaine, mode, règles de l'arbitre. |
| `AiCamera(Id, Label, Role, AudioChannel)` | Une caméra (une scène dans Studio). `Id` libre, renvoyé tel quel dans les suggestions. |
| `AiDecisionMode.Suggest` / `Auto` | Suggest : l'app affiche, l'opérateur décide (mode « agent »). Auto : l'arbitre du SDK coupe (mode « auto »). |
| `AiSuggestion` | Plan suggéré : caméra, confiance, raison, latence, scores, verdict de l'arbitre (Auto). |
| `AiOnAirChange` | Ce qui est à l'antenne a changé : `Initial`, `Ai` (Auto, l'app doit basculer), `Manual`. |
| `LocalWorkerProvider` | Le worker tourne en local (process enfant, gRPC sur named pipe, audio et vidéo en mémoire partagée). |

Méthodes de `AiClient` :

- `StartAsync(session)`, `StopAsync()`, `RestartAsync(session)` (changement de caméras : le worker repart de zéro) ;
- `ReportOnAir(cameraId)` : **à appeler à chaque coupe faite par l'app ou l'opérateur** (le worker raisonne sur le plan courant ; en Auto, ça verrouille les coupes IA pendant `ManualLockMs`) ;
- `WriteAudio(channel, samples, sampleRate)` : audio d'un micro, mono float32, n'importe quel taux, n'importe quel thread (rééchantillonné en 16 kHz et cadencé par le SDK) ;
- `WriteVideo(cameraId, bgra, width, height, stride)` : frame d'une caméra, BGRA8, n'importe quelle taille, n'importe quel thread (un producteur par caméra) ; mise à la taille de `VideoFormat` et limitée à son fps par le SDK. Sans effet si le worker ne lit pas de vidéo (podcast) ;
- `SendPayload(type, cameraId, fields)` : indications propres au domaine (types listés dans `Capabilities.PayloadTypes`).

Événements : `SuggestionReceived`, `OnAirChanged`, `StateChanged` (dont redémarrage automatique après crash du worker),
`SessionStarted`, `StatsReceived` (métriques décrites par `Capabilities.Metrics`), `LogReceived`.
Ils sont levés via `AiClientOptions.Dispatch` s'il est fourni (sinon sur un thread du SDK).

## Paquet NuGet

`Castor.AI` et `Castor.AI.Contracts` sont publiés sur GitHub Packages (`Castor-Studio`) par
`.github/workflows/nuget-publish.yml` à chaque tag `v*.*.*` (version du paquet = tag sans le `v`) :

```bash
git tag v1.0.0
git push origin v1.0.0
```

Le même tag publie aussi l'exécutable du worker en asset de la release GitHub du tag
(`.github/workflows/worker-release.yml`, voir « Déploiement »).

Sur les PR vers `main` / `dev`, `.github/workflows/ci.yml` teste et emballe seulement (paquets en artifact, version `0.0.0-ci.<run>`).

Côté Castor Studio, ajouter la source dans `nuget.config` :

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

`GITHUB_TOKEN` : en local un PAT avec `read:packages`, en CI le `GITHUB_TOKEN` du workflow
(`permissions: packages: read`). Puis référencer seulement `Castor.AI` (`Castor.AI.Contracts` suit en dépendance) :

```xml
<PackageReference Include="Castor.AI" Version="1.0.0" />
```

## Intégration dans Castor Studio

### 1. Enregistrement (`ServiceCollectionExtensions.AddCommonServices`)

```csharp
services.AddCastorAI(options =>
{
    options.Worker = new LocalWorkerOptions
    {
        // Worker + packs/ de modèles livrés à côté de l'app (voir « Déploiement »).
        WorkerPath = Path.Combine(AppContext.BaseDirectory, "ai", "Castor.AI.Worker.exe"),
    };
    options.Client = new AiClientOptions { Dispatch = action => Dispatcher.UIThread.Post(action) };
});
```

`AiClient` est alors un singleton injectable (remplace `IAiAnalysisClient` / `GrpcAiAnalysisClient`).

### 2. Session (`MulticamViewModel.StartAiAsync`)

```csharp
var session = new AiSessionOptions
{
    Domain = GetSelectedModuleName(),                     // "podcast", "football"
    Mode = mode == "auto" ? AiDecisionMode.Auto : AiDecisionMode.Suggest,
    Cameras = selectedScenes
        .Select(scene => new AiCamera(scene.Id.ToString("N"), scene.Name /*, role, micro */))
        .ToList(),
    InitialCameraId = _workspace.SelectedScene?.Id.ToString("N"),
};
await _ai.StartAsync(session);
```

### 3. Réagir

```csharp
_ai.SuggestionReceived += (_, suggestion) =>
{
    // Mode agent : afficher la suggestion (scène, confiance, raison).
    if (suggestion.Verdict is null) ShowSuggestion(suggestion.CameraId, suggestion.Confidence);
};

_ai.OnAirChanged += (_, change) =>
{
    // Mode auto : l'arbitre a accepté, Studio bascule.
    if (change.Source == AiOnAirSource.Ai) _workspace.SelectScene(SceneById(Guid.ParseExact(change.CameraId, "N")));
};

_ai.StateChanged += (_, change) => AiStatusText = $"{change.State}: {change.Detail}";
```

Et à chaque changement de scène fait par l'opérateur (ou suggestion appliquée en mode agent) :

```csharp
_ai.ReportOnAir(scene.Id.ToString("N"));
```

### 4. Audio

Un canal par micro. `AiCamera.AudioChannel` relie la caméra à son micro. Studio branche le callback audio
de chaque source OBS concernée (`obs_source_add_audio_capture_callback`) :

```csharp
_ai.WriteAudio(channel, monoSamples, sampleRate);   // 48 kHz OBS → 16 kHz fait par le SDK
```

Sans audio (`AudioChannel = -1` partout), le worker podcast se contente des payloads `speaker.*` / `camera.metadata`.

### 5. Vidéo (football)

Le worker football lit une frame par caméra. `AiClient.VideoFormat` donne la taille voulue (640x360 BGRA, 15 fps) :
produire directement à cette taille évite la mise à l'échelle du SDK.

```csharp
// Pour chaque scène de la session, depuis un callback vidéo brut OBS (BGRA) :
_ai.WriteVideo(scene.Id.ToString("N"), bgra, width, height, linesize);
```

Aujourd'hui Studio pousse un flux encodé par scène vers une URL (`LibObsAiSceneStreamRuntime`) : en local il faut
à la place un rendu brut par scène (sortie vidéo indépendante + `obs_add_raw_video_callback` ou équivalent).

### Déploiement

Le SDK lance `Castor.AI.Worker.exe`. Studio livre le worker dans un dossier `ai/` à côté de l'app
(`LocalWorkerOptions.WorkerPath`, voir l'enregistrement plus haut).

Chaque tag `v*.*.*` de ce repo crée une release GitHub avec deux assets (`.github/workflows/worker-release.yml`) :

| Asset | Contenu |
|---|---|
| `Castor.AI.Worker-<version>-win-x64.zip` | `dotnet publish -r win-x64 --self-contained` : `Castor.AI.Worker.exe`, runtime .NET et DLL natives ONNX Runtime. Aucun runtime à installer sur la machine. Sans modèle. |
| `Castor.AI.Worker-<version>-win-x64.zip.sha256` | `<sha256>  <fichier>` (format `sha256sum`). |

`<version>` = tag sans le `v`, la même que le paquet `Castor.AI` du tag. **Toujours prendre le worker de la version
de `Castor.AI` référencée par Studio** : ils partagent `castor_ai.proto` et les formats de mémoire partagée.

Dans le pipeline de build / installeur de Studio (PowerShell, version lue depuis la `PackageReference` de `Castor.AI`) :

```powershell
$version = "1.0.0"
$asset = "Castor.AI.Worker-$version-win-x64.zip"
gh release download "v$version" --repo Castor-Studio/Castor-AI-Worker --pattern "$asset*" --dir build/ai
$expected = (Get-Content "build/ai/$asset.sha256").Split(" ")[0]
$actual = (Get-FileHash "build/ai/$asset" -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "$asset : SHA-256 $actual, attendu $expected" }
Expand-Archive "build/ai/$asset" -DestinationPath <sortie de l'app>/ai -Force
```

Sans `gh`, l'URL directe est `https://github.com/Castor-Studio/Castor-AI-Worker/releases/download/v<version>/<asset>`
(repo privé : jeton avec accès en lecture au repo, `gh` le gère).

Pas de modèle dans l'installeur : le worker télécharge au démarrage ceux de son domaine qui manquent
(voir `docs/architecture.md`, section 9). Pendant le téléchargement, `StateChanged` remonte
`Starting` avec la progression (« downloading football/checkpoint_5_416.onnx 42 % ... ») : à afficher dans l'UI IA.
`LocalWorkerOptions.ReadyTimeout` compte depuis la dernière progression, un long téléchargement n'expire donc pas.

Le worker s'arrête tout seul si Studio meurt (surveillance du PID parent).

## Ce qui reste côté Studio

- UI de choix du micro par scène (`AudioChannel`) et, pour le podcast, du rôle (`Role`) ;
- capture audio OBS par micro vers `WriteAudio` ;
- rendu brut par scène vers `WriteVideo` (football) ;
- téléchargement du zip du worker (version de `Castor.AI`) dans l'installeur, voir « Déploiement ».
