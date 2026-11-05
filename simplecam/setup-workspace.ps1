param(
    [string]$CcsRoot = 'C:\ti\ccs1210\ccs',
    [string]$Workspace,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Workspace) { $Workspace = Join-Path $repo 'workspaces\simplecam' }
$Workspace = [IO.Path]::GetFullPath($Workspace)
$eclipsec = Join-Path $CcsRoot 'eclipse\eclipsec.exe'
if (-not (Test-Path -LiteralPath $eclipsec -PathType Leaf)) {
    throw "CCS launcher not found: $eclipsec. Supply -CcsRoot with your CCS directory."
}

function Import-CcsProject([string]$Name) {
    $projectPath = Join-Path $repo $Name
    $entry = Join-Path $Workspace ".metadata\.plugins\org.eclipse.core.resources\.projects\$Name"
    if (Test-Path -LiteralPath $entry -PathType Container) {
        $location = Join-Path $entry '.location'
        $registeredPath = Join-Path $Workspace $Name
        if (Test-Path -LiteralPath $location -PathType Leaf) {
            $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($location))
            $match = [regex]::Match($text, 'URI//(file:[^\x00]+)')
            if (-not $match.Success) {
                throw "Cannot verify the existing CCS project location for $Name. Use a fresh -Workspace."
            }
            $registeredPath = ([uri]$match.Groups[1].Value).LocalPath
        }
        if ([IO.Path]::GetFullPath($registeredPath) -ne [IO.Path]::GetFullPath($projectPath)) {
            throw "Workspace already contains $Name from '$registeredPath'. Use a different -Workspace."
        }
        Write-Host "Already imported: $Name"
        return
    }
    & $eclipsec '-noSplash' '-data' $Workspace '-application' 'com.ti.ccstudio.apps.projectImport' `
        '-ccs.location' $projectPath
    if ($LASTEXITCODE -ne 0) { throw "CCS import failed for $Name (exit $LASTEXITCODE)." }
}

foreach ($name in @('wisp-base', 'HimaxCamera', 'simplecam')) {
    Import-CcsProject $name
}

if ($Build) {
    & $eclipsec '-noSplash' '-data' $Workspace '-application' 'com.ti.ccstudio.apps.projectBuild' `
        '-ccs.projects' 'simplecam' '-ccs.configuration' $Configuration `
        '-ccs.buildType' 'full' '-ccs.autoOpen' '-ccs.listProblems'
    if ($LASTEXITCODE -ne 0) { throw "CCS build failed (exit $LASTEXITCODE)." }
    $output = Join-Path $PSScriptRoot "$Configuration\simplecam.out"
    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
        throw "CCS did not produce the expected firmware: $output"
    }
    $map = Get-Content -LiteralPath (Join-Path $PSScriptRoot "$Configuration\simplecam.map") -Raw
    if ($map -notmatch '(?im)\.camera_frame\s*\r?\n\s*\*\s+0\s+00007000\s+00004d34\s+UNINITIALIZED') {
        throw 'Unexpected camera frame allocation: expected 19,764 NOINIT bytes at 0x7000.'
    }
    if ($map -notmatch '(?im)^\s*0000ffe2\s+00000002\s+isr-link\.obj\s+\(\.int41\)') {
        throw 'The camera timeout interrupt vector .int41 is missing or has an unexpected owner.'
    }
    Write-Host "Verified firmware: $output"
}
Write-Host "CCS workspace: $Workspace"
Write-Host 'Open this workspace in CCS and select simplecam when programming the WISP.'
