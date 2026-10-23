---
_layout: landing
---

# Documentation Castor AI

Castor AI est l'IA de réalisation de Castor Studio : un process **worker** choisit en direct la caméra à
mettre à l'antenne, à partir de la vidéo des caméras, de l'audio des micros et des indications envoyées par
l'app. L'app Avalonia (Castor Studio, ou la console de debug `Castor.AI.Dashboard`) ne parle au worker qu'à
travers le SDK `Castor.AI`. Ce site est construit en local depuis le repo ; rien n'est hébergé.

- [Fonctionnement du worker](guides/worker.md) : lancement, démarrage et modèles, service gRPC, déroulé d'une
  session, stratégies par domaine, pannes.
- [Communiquer depuis l'app Avalonia](guides/avalonia.md) : enregistrer `AiClient`, ouvrir une session,
  pousser audio, vidéo et payloads, réagir aux suggestions sur le thread UI, cycle de vie et déploiement.
- [Architecture et formats](architecture.md) : vue d'ensemble local / cloud, diagramme de séquence, temps
  réel, format de la mémoire partagée, workers Podcast et Football, hébergement des modèles.
- [API reference](api/Castor.AI.yml) : l'API C# publique du SDK (`Castor.AI`) et du contrat
  (`Castor.AI.Contracts`), générée depuis les commentaires XML.

La présentation du repo et l'intégration pas à pas dans Castor Studio sont aussi dans le
[README](https://github.com/Castor-Studio/Castor-AI-Worker#readme) et le
[README du SDK](https://github.com/Castor-Studio/Castor-AI-Worker/blob/main/Castor.AI/README.md).

```
App Avalonia (Castor Studio, Castor.AI.Dashboard)
  └ AiClient (SDK Castor.AI)            session, horloge, arbitre, flux audio et vidéo
      └ LocalWorkerProvider             lance et supervise le worker
          ├ gRPC sur named pipe         contrôle, payloads, suggestions, stats, logs
          └ mémoire partagée            frames vidéo (1 région par caméra) + ring audio
              └ Castor.AI.Worker.exe    un domaine par process : podcast, football
```

## Construire ce site

Prérequis : SDK .NET 10 et PowerShell 7. Depuis la racine du repo :

```powershell
pwsh eng/Build-Docs.ps1 -Serve
```

Le site est servi sur `http://localhost:8080`. Sans `-Serve`, il est seulement écrit dans `docs/_site/`.
DocFX compile `Castor.AI` et `Castor.AI.Contracts` pour générer l'API reference.

Les dossiers générés (`docs/api/`, `docs/_site/`) ne sont pas commités.
