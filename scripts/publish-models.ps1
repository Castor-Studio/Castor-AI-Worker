# Publishes the models of Castor.AI.Worker/Models/models.json as assets of its release, where the worker
# downloads them (ModelProvisioner). Needs gh (authenticated) and, for football, the exported file
# (scripts/export-football-model.ps1).
#   pwsh scripts/publish-models.ps1 [-DryRun]
# Assets of a release are never replaced: a new model gets a new file name, SHA-256 and release tag.
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
# Single source of truth for the repository, the tag and the pinned files.
$manifest = Get-Content (Join-Path $root 'Castor.AI.Worker/Models/models.json') -Raw | ConvertFrom-Json
$repository = $manifest.repository
$tag = $manifest.release_tag

$assets = foreach ($model in $manifest.models) {
    $path = Join-Path $root "packs/$($model.domain)/models/$($model.file)"
    if (-not (Test-Path $path) -and $model.upstream) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
        Write-Host "[get]  $($model.file) <- $($model.upstream)"
        Invoke-WebRequest -Uri $model.upstream -OutFile $path -UseBasicParsing
    }

    if (-not (Test-Path $path)) {
        throw "$path missing (football: scripts/export-football-model.ps1)"
    }

    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $model.sha256.ToLowerInvariant()) {
        throw "$($model.file): SHA-256 $hash, models.json pins $($model.sha256). Publish the pinned file or update models.json."
    }

    Write-Host "[ok]   $($model.domain)/$($model.file) matches models.json"
    $path
}

$json = gh release view $tag --repo $repository --json assets 2>$null
$releaseExists = $LASTEXITCODE -eq 0
$existing = if ($releaseExists) { @(($json | Out-String | ConvertFrom-Json).assets.name) } else { @() }

$missing = @($assets | Where-Object { $existing -notcontains (Split-Path -Leaf $_) })
if ($missing.Count -eq 0) {
    Write-Host "[ok]   release $tag of $repository already has every model"
    return
}

if ($DryRun) {
    Write-Host "[dry]  would publish to $repository release ${tag}: $($missing | ForEach-Object { Split-Path -Leaf $_ })"
    return
}

if (-not $releaseExists) {
    $notes = "Models downloaded by Castor.AI.Worker at startup (pinned by SHA-256 in Castor.AI.Worker/Models/models.json). " +
             "silero_vad.onnx: Silero VAD v6.2.1, MIT license (https://github.com/snakers4/silero-vad). " +
             "checkpoint_5_416.onnx: Castor-AI football checkpoint_5.pt exported to ONNX (416x416)."
    gh release create $tag --repo $repository --title "Models $tag" --notes $notes @missing
}
else {
    gh release upload $tag --repo $repository @missing
}

if ($LASTEXITCODE -ne 0) { throw "gh failed (exit code $LASTEXITCODE)" }
Write-Host "[ok]   published to https://github.com/$repository/releases/tag/$tag"
