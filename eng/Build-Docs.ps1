#Requires -Version 7.2
<#
.SYNOPSIS
Builds the Castor AI DocFX documentation locally.

.DESCRIPTION
Restores the pinned docfx tool, then builds the site into docs/_site/. The API reference
is generated from the XML doc comments of Castor.AI and Castor.AI.Contracts. With -Serve,
the site is served on http://localhost:<Port>. With -WarningsAsErrors (used by CI), any
DocFX warning such as a broken link or an invalid cross reference fails the build.
#>
[CmdletBinding()]
param(
    [switch] $Serve,
    [int] $Port = 8080,
    [switch] $WarningsAsErrors
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repositoryRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    $docfxArguments = @('docfx', (Join-Path 'docs' 'docfx.json'))
    if ($WarningsAsErrors) {
        $docfxArguments += '--warningsAsErrors'
    }
    if ($Serve) {
        $docfxArguments += @('--serve', '--port', $Port)
    }
    dotnet @docfxArguments
    if ($LASTEXITCODE -ne 0) { throw 'docfx failed.' }
}
finally {
    Pop-Location
}
