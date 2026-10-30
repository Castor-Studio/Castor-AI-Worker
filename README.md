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
| `Castor.AI.Dashboard.Tests` | Tests des aperçus vidéo du dashboard (pixels, rafraîchissement, arrêt et concurrence). |
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

Pour tout vérifier depuis l'interface, ouvrir le dashboard, aller dans **Diagnostics**, choisir la carte GPU par son nom (0 par défaut), puis cliquer sur **Run CPU/GPU checks**. Le tableau affiche le fournisseur réellement utilisé, les opérateurs CPU/GPU, la parité des sorties et les latences. Pour les sessions normales, choisir **Mode session : gpu** ou **cpu** dans la barre du haut ; le choix de carte dans Diagnostics s'applique aux prochaines sessions GPU. Les progrès et les replis s'affichent dans **Logs**, que l'on peut filtrer, sélectionner et copier avec **Copier les logs**.

Les modèles utilisent le GPU via DirectML par défaut sous Windows, avec repli CPU journalisé
si le GPU ne peut pas être initialisé. Le mode CPU est accessible dans le dashboard ou avec
`dotnet run --project Castor.AI.Dashboard -- --device cpu`.
L'onglet Logs propose une recherche par heure, source ou message.
Dans **Dashboard**, glisser les séparateurs pour ajuster la hauteur des caméras et de la timeline,
ainsi que la largeur des décisions, graphiques et logs. Les panneaux conservent un défilement quand leur contenu dépasse.
L'onglet **Rendu visuel** affiche en grand la caméra à l'antenne et, en bas, les sources vidéo de chaque caméra.
Les aperçus reprennent les images acceptées par le SDK pour l'IA (rafraîchies jusqu'à 10 fois par seconde),
avec un cadre rouge sur la caméra à l'antenne. **Take on air** permet une bascule manuelle.
Le séparateur horizontal ajuste la hauteur des miniatures. Choisir les fichiers dans **Dashboard**,
puis démarrer une session : les caméras sans vidéo ou les workers sans entrée vidéo affichent un emplacement vide.
Comparaison CPU/GPU : `dotnet run --project scripts/benchmark-inference/Benchmark.csproj -c Release -- 30`.

Aucun script à lancer pour les modèles : le worker (et les tests) téléchargent ceux qui manquent depuis la
release `models-v1` de ce repo, vérifiés par SHA-256. Publier un nouveau modèle : `scripts/publish-models.ps1`
(voir `docs/architecture.md`, section 9).
