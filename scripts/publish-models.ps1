# Publishes the models of ModelCatalog as assets of the models release of this repository, where the
# worker downloads them (Castor.AI.Worker/Models/ModelProvisioner.cs). Needs gh (authenticated) and,
# for football, the exported file (scripts/export-football-model.ps1).
#   pwsh scripts/publish-models.ps1 [-DryRun]
# Assets of a release are never replaced: a new model gets a new file name, SHA-256 and release tag.
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$repository = 'Castor-Studio/Castor.AI.Worker'
$catalogFile = Join-Path $root 'Castor.AI.Worker/Models/ModelProvisioner.cs'
$catalog = Get-Content $catalogFile -Raw

# Single source of truth: the tag and the pinned files are read from ModelCatalog.
$tag = [regex]::Match($catalog, 'ReleaseTag = "([^"]+)"').Groups[1].Value
$specs = [regex]::Matches($catalog, 'new\(\s*"([^"]+)",\s*"([^"]+)",\s*"([0-9a-f]{64})"') | ForEach-Object {
    @{ Domain = $_.Groups[1].Value; File = $_.Groups[2].Value; Sha256 = $_.Groups[3].Value }
}
if (-not $tag -or $specs.Count -eq 0) { throw "Cannot read ModelCatalog in $catalogFile" }

# Upstream sources for models this repository does not produce.
$upstream = @{
    'silero_vad.onnx' = 'https://github.com/snakers4/silero-vad/raw/v6.2.1/src/silero_vad/data/silero_vad.onnx'
}

$assets = foreach ($spec in $specs) {
    $path = Join-Path $root "packs/$($spec.Domain)/models/$($spec.File)"
    if (-not (Test-Path $path) -and $upstream.ContainsKey($spec.File)) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
        Write-Host "[get]  $($spec.File) <- $($upstream[$spec.File])"
        Invoke-WebRequest -Uri $upstream[$spec.File] -OutFile $path -UseBasicParsing
    }

    if (-not (Test-Path $path)) {
        throw "$path missing (football: scripts/export-football-model.ps1)"
    }

    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $spec.Sha256) {
        throw "$($spec.File): SHA-256 $hash, ModelCatalog pins $($spec.Sha256). Publish the pinned file or update ModelCatalog."
    }

    Write-Host "[ok]   $($spec.Domain)/$($spec.File) matches ModelCatalog"
    $path
}

$json = gh release view $tag --repo $repository --json assets 2>$null
$releaseExists = $LASTEXITCODE -eq 0
$existing = if ($releaseExists) { @(($json | Out-String | ConvertFrom-Json).assets.name) } else { @() }

$missing = @($assets | Where-Object { $existing -notcontains (Split-Path -Leaf $_) })
if ($missing.Count -eq 0) {
    Write-Host "[ok]   release $tag already has every model"
    return
}

if ($DryRun) {
    Write-Host "[dry]  would publish to $repository release ${tag}: $($missing | ForEach-Object { Split-Path -Leaf $_ })"
    return
}

if (-not $releaseExists) {
    $notes = "Models downloaded by Castor.AI.Worker at startup (pinned by SHA-256 in ModelCatalog). " +
             "silero_vad.onnx: Silero VAD v6.2.1, MIT license (https://github.com/snakers4/silero-vad). " +
             "checkpoint_5_416.onnx: Castor-AI football checkpoint_5.pt exported to ONNX (416x416)."
    gh release create $tag --repo $repository --title "Models $tag" --notes $notes @missing
}
else {
    gh release upload $tag --repo $repository @missing
}

if ($LASTEXITCODE -ne 0) { throw "gh failed (exit code $LASTEXITCODE)" }
Write-Host "[ok]   published to https://github.com/$repository/releases/tag/$tag"
