# Exports the football ball detector (Castor-AI packages/football/models/checkpoint_5.pt, YOLOv9c COCO)
# to packs/football/models/checkpoint_5_416.onnx for the C# worker.
# Needs a Castor-AI checkout with its uv environment installed (uv sync), and uv in the PATH.
#   pwsh scripts/export-football-model.ps1 [-CastorAi ../Castor-AI]
param(
    [string]$CastorAi = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Castor-AI')
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$checkpoint = Join-Path $CastorAi 'packages/football/models/checkpoint_5.pt'
# The checkpoint the C# port was checked against (Castor-AI dev, 8bfef18).
$checkpointSha256 = '404c350d91f1a81e88af158f1331d5af80154f87a4c824b559983f3cb58a0c25'
$imageSize = 416 # DETECTION_IMAGE_SIZE
$target = Join-Path $root 'packs/football/models/checkpoint_5_416.onnx'

if (-not (Test-Path $checkpoint)) {
    throw "Checkpoint not found: $checkpoint. Pass -CastorAi <path to the Castor-AI repository>."
}

$hash = (Get-FileHash $checkpoint -Algorithm SHA256).Hash
if ($hash -ne $checkpointSha256) {
    Write-Warning "checkpoint_5.pt changed (SHA-256 $hash): check the C# port against the new model."
}

if ((Test-Path $target) -and ((Get-Item $target).LastWriteTime -gt (Get-Item $checkpoint).LastWriteTime)) {
    Write-Host "[ok]   football/checkpoint_5_416.onnx already exported"
    return
}

Write-Host "[run]  export $checkpoint -> $target"
# --no-sync: use the Castor-AI environment as installed; onnx/onnxslim only live in a temporary overlay.
Push-Location $CastorAi
try {
    uv run --no-sync --with onnx --with onnxslim python -I (Join-Path $PSScriptRoot 'export_football_model.py') $checkpoint $target $imageSize
    if ($LASTEXITCODE -ne 0) { throw "Export failed (exit code $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

Write-Host "[ok]   football/checkpoint_5_416.onnx"
