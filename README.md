# Castor.AI.Worker

IA de réalisation de Castor Studio : choisit en direct la caméra à mettre à l'antenne, à partir de la vidéo
des caméras, de l'audio des micros et des indications envoyées par l'app. Castor Studio vit dans son propre
repo et utilise celui-ci à travers le SDK client et l'exécutable du worker.

| Projet | Rôle |
|---|---|
| `Castor.AI` | SDK client référencé par Castor Studio (`AiClient`) : lance le worker, lui envoie audio et vidéo, remonte les plans suggérés. Voir [Castor.AI/README.md](Castor.AI/README.md). |
| `Castor.AI.Contracts` | Contrat gRPC (`castor_ai.proto`) et formats de mémoire partagée, communs au SDK et au worker. |
| `Castor.AI.Worker` | Process worker, un domaine par process : `podcast` (Silero VAD), `football` (détection du ballon, YOLO). |
| `Castor.AI.Dashboard` | Console de debug Avalonia pour l'équipe IA, pilotée par `profiles/*.json`. |
| `Castor.AI.Tests`, `Castor.AI.Worker.Tests` | Tests xUnit, dont la parité avec les modules Python de Castor-AI. |

Architecture, format de la mémoire partagée, stratégies par domaine et gestion des modèles :
[docs/architecture.md](docs/architecture.md).

## Démarrer

SDK .NET 10 sous Windows. ffmpeg (dans le `PATH` ou `CASTOR_FFMPEG`) n'est utile qu'à la console de debug,
pour lire des fichiers de test.

```
dotnet test Castor.AI.Worker.sln
dotnet run --project Castor.AI.Dashboard
```

Aucun script à lancer pour les modèles : le worker (et les tests) téléchargent ceux qui manquent depuis la
release `models-v1` de ce repo, vérifiés par SHA-256. Publier un nouveau modèle : `scripts/publish-models.ps1`
(voir `docs/architecture.md`, section 9).
