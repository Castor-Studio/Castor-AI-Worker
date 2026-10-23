# Fonctionnement du worker

`Castor.AI.Worker` est un process console .NET 10 (ASP.NET Core + ONNX Runtime). Un process sert **un seul
domaine** (`podcast`, `football`), choisi au lancement. Il expose le service gRPC `AiWorker` sur un named pipe,
lit les médias en mémoire partagée et renvoie des suggestions de plan (`SceneDecision`).

Le worker ne décide jamais seul de ce qui passe à l'antenne : il **suggère**. L'app reste l'autorité finale
(arbitre du SDK ou opérateur) et lui renvoie ce qui est réellement à l'antenne (`CameraOnAir`).

L'app ne lance pas le worker elle-même : `LocalWorkerProvider` (SDK `Castor.AI`) s'en charge. Voir
[Communiquer depuis l'app Avalonia](avalonia.md).

## Lancement

```
Castor.AI.Worker.exe --domain podcast|football --pipe <nom> [--parent-pid <pid>]
```

| Argument | Défaut | Rôle |
|---|---|---|
| `--domain` | `podcast` | Domaine servi par ce process. Inconnu : sortie immédiate avec le code 2. |
| `--pipe` | `castor-ai-worker` | Nom du named pipe d'écoute. Le SDK en tire un unique par session : `castor-ai-<guid>`. |
| `--parent-pid` | aucun | PID de l'app. Le worker s'arrête quand ce process disparaît (`ParentWatchdog`). |

Codes de sortie :

| Code | Cause |
|---|---|
| 0 | Arrêt normal (parent disparu, arrêt demandé). |
| 2 | Domaine inconnu. Vérifié avant de toucher aux modèles ou au pipe (sert de smoke test dans `worker-release.yml`). |
| 3 | Modèle introuvable, téléchargement ou checksum en échec (`Warmup.ModelLoadFailedExitCode`). |

Variables d'environnement lues par le worker (détail dans [Architecture, section 9](../architecture.md#9-modèles--installation-et-téléchargement)) :

| Variable | Rôle |
|---|---|
| `CASTOR_PACKS_DIR` | Dossier de packs de modèles installés. |
| `CASTOR_MODELS_DIR` | Cache des modèles téléchargés (défaut `%LOCALAPPDATA%\Castor\models`). |
| `CASTOR_MODELS_URL` | Miroir de téléchargement : `<url>/<fichier>`. |
| `CASTOR_MODELS_MANIFEST` | Remplace le `models.json` embarqué, sans rebuild. |

`stdout` et `stderr` du worker sont relus par le SDK et remontés dans `AiClient.LogReceived`
(`[worker stdout] ...`). Les logs ASP.NET Core sont filtrés au niveau `Warning`.

Lancer un worker à la main, pour le déboguer sans app :

```powershell
dotnet run --project Castor.AI.Worker -- --domain football --pipe castor-debug
```

## Démarrage

```
Program.cs
  ├ WorkerOptions.Parse(args)           --domain, --pipe, --parent-pid
  ├ Kestrel ListenNamedPipe(HTTP/2)     service gRPC AiWorker
  ├ Warmup (BackgroundService)          modèles du domaine, puis Health = READY
  └ ParentWatchdog (BackgroundService)  arrêt si l'app meurt
```

1. Kestrel écoute tout de suite sur le pipe : `Health` répond pendant le chargement.
2. `Warmup` prend chaque modèle de `StrategyCatalog.RequiredModels` : pack installé, sinon cache, sinon
   téléchargement vérifié par SHA-256 (`ModelProvisioner`). La progression va dans `Health.detail`
   (« downloading football/checkpoint_5_416.onnx 42 % »), puis les modèles sont chargés dans ONNX Runtime
   (`ModelStore`).
3. `WorkerState.Status` passe de `STATUS_STARTING` à `STATUS_READY`. Avant ça, toute session est refusée
   (`CODE_INTERNAL`, « Worker not ready »).

Les modèles sont chargés **une fois par process** et partagés entre sessions ; chaque session crée sa
propre stratégie (états VAD, focus...).

## Service gRPC

Contrat : `Castor.AI.Contracts/Protos/castor_ai.proto` (package `castor.ai.v1`), implémenté par
`AiWorkerService`.

| RPC | Réponse |
|---|---|
| `Health` | `status` (`STARTING`, `READY`), `active_sessions`, `detail` (progression du démarrage). Appelé en boucle par le SDK jusqu'à `READY`. |
| `GetCapabilities` | `WorkerCapabilities` du domaine : entrées requises, `max_cameras`, `preferred_video`, `preferred_audio`, `payload_types`, métriques et flags publiés dans les stats. |
| `Session` | Stream bidirectionnel, un par session. Premier message client obligatoire : `SessionStart`. |

Validation de `SessionStart`. Chaque refus est un `SessionError` avec `fatal = true`, puis le stream se ferme :

| Condition | Code |
|---|---|
| Premier message autre que `start` | `CODE_BAD_FORMAT` |
| Worker pas encore `READY` | `CODE_INTERNAL` |
| `domain` différent de celui du process | `CODE_UNSUPPORTED_DOMAIN` |
| Aucune caméra | `CODE_BAD_FORMAT` |
| Plus de caméras que `max_cameras` | `CODE_TOO_MANY_CAMERAS` |
| Régions de mémoire partagée impossibles à ouvrir | `CODE_TRANSPORT_FAILED` |
| La stratégie rejette la session (paramètre invalide, format vidéo...) | `CODE_BAD_FORMAT` |

Sinon le worker répond `SessionAccepted` (avec ses capabilities) et un `WorkerLog` de démarrage.

## Déroulé d'une session

`SessionRunner` fait tourner la session sur trois boucles découplées, pour qu'un modèle lent ne bloque jamais
la réception :

```
stream app ──► boucle de lecture ──► stratégie (OnPayload, OnAir) ──► réveil
                                                                        │
            boucle d'inférence ◄── réveil (capacité 1, latest-wins) ou tick 100 ms
                 │ strategy.Evaluate(now) ─► SceneDecision ?
                 ▼
            outbox ◄── boucle de stats (toutes les 250 ms) : WorkerStats
                 │
            boucle d'écriture : seule à écrire sur le stream (écritures gRPC non concurrentes)
```

| Message reçu (`ClientMessage`) | Effet côté worker |
|---|---|
| `payload` (`PayloadEvent`) | `strategy.OnPayload`, puis réveil de l'inférence. |
| `on_air` (`CameraOnAir`) | `strategy.OnAir` : le plan réellement à l'antenne (IA ou manuel). |
| `video_frame` (`VideoFrameReady`) | Réveil : la stratégie lit la **dernière** frame de la région (latest-wins). |
| `audio` (`AudioReady`) | Réveil : la VAD consomme le ring audio sans attendre le tick. |
| `stop` (`SessionStop`) | Fin de la boucle de lecture, `SessionEnded` avec la raison. |
| `start` en double | Ignoré, `WorkerLog` d'avertissement. |

| Message envoyé (`WorkerMessage`) | Quand |
|---|---|
| `accepted` | Une fois, après validation et `strategy.Start`. |
| `decision` (`SceneDecision`) | Quand `Evaluate` a une nouvelle suggestion ; `processing_ms` = temps de calcul. |
| `stats` (`WorkerStats`) | Toutes les 250 ms : `inference_ms_avg`, puis métriques et flags remplis par la stratégie. |
| `log` (`WorkerLog`) | Démarrage de session, avertissements. |
| `error` (`SessionError`) | Refus de la session (voir plus haut). |
| `ended` (`SessionEnded`) | Toujours, en fin de session : arrêt demandé, stream fermé ou app déconnectée. |

**Horloge.** Le worker n'a pas d'horloge propre : son `SessionClock` suit le plus grand `pts_us` reçu de
l'app (microsecondes depuis `SessionStart`). Toute décision porte `source_pts_us`, le pts de l'entrée la plus
récente utilisée : l'app en déduit la latence et jette les décisions périmées.

**Latest-wins.** Plusieurs entrées entre deux évaluations ne donnent qu'un réveil ; la stratégie lit toujours
la frame la plus récente. Aucune file d'attente, le retard ne s'accumule jamais. Le format des régions et
l'algorithme de lecture sont décrits dans [Architecture, section 4](../architecture.md#4-format-shared-memory).

**Décision refusée.** Une suggestion non suivie (l'app renvoie un autre `CameraOnAir`) est reproposée après
1 s ; le worker raisonne toujours sur le plan que l'app a réellement pris.

## Stratégies par domaine

La logique métier d'un domaine implémente `IDomainStrategy`. `SessionRunner` l'appelle sous un verrou,
jamais en parallèle :

| Méthode | Rôle |
|---|---|
| `Start(SessionStart, SessionMedia, log)` | Lit caméras, rôles et `params` ; lève une exception pour refuser la session. |
| `OnPayload(PayloadEvent)` | Indications de l'app (`speaker.start`, `camera.metadata`...). |
| `OnAir(CameraOnAir)` | Plan réellement à l'antenne. |
| `Evaluate(nowUs)` | Consomme les médias en attente, renvoie une `SceneDecision` ou `null`. |
| `FillStats(WorkerStats)` | Métriques et flags déclarés dans les capabilities. |

| Domaine | Entrées | Modèle | Détail |
|---|---|---|---|
| `podcast` | audio (1 canal par micro), payloads | Silero VAD | [Architecture, section 7](../architecture.md#7-worker-podcast) |
| `football` | vidéo 640x360 BGRA8 à 15 fps, 4 caméras max | YOLO (ballon) | [Architecture, section 8](../architecture.md#8-worker-football) |

Ajouter un domaine :

1. une stratégie `Strategies/<Domaine>/` qui implémente `IDomainStrategy`, avec un `Describe(workerId)` qui
   renvoie ses `WorkerCapabilities` ;
2. l'enregistrer dans `StrategyCatalog` (`KnownDomains`, `Capabilities`, `RequiredModels`, `LoadModels`,
   `Create`) ;
3. ses modèles dans `Models/models.json` (SHA-256 épinglé), publiés par `scripts/publish-models.ps1` ;
4. un profil `profiles/<domaine>.json` pour le tester dans `Castor.AI.Dashboard`.

L'app n'a rien à changer : elle s'adapte aux capabilities (caméras max, format vidéo et audio, payloads,
métriques).

## Pannes

- **App disparue** : `ParentWatchdog` arrête le worker, qui ne garde jamais le GPU ou le pipe en orphelin.
- **Stream coupé par l'app** : la session se termine proprement, les régions sont démappées ; le process
  reste prêt pour une autre session.
- **Crash du worker** : vu par le SDK (process terminé ou stream coupé), qui relance un worker neuf avec backoff
  (1 s, 2 s, 5 s) et rejoue la session. Voir [Pannes et redémarrages](avalonia.md#cycle-de-vie-et-pannes).
- **Modèle indisponible** : code de sortie 3 et message explicite dans les logs, remonté par le SDK.
