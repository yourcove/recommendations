param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$DestinationRoot = (Join-Path $env:LOCALAPPDATA "cove_personal_live\extensions"),
    [switch]$SkipUi,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

# Extensions that ship a DLL (manifest-only bundles can be added here with a $true flag later).
$extensions = @(
    "Recommendations.Core",
    "Recommendations.Tastes"
)

if (-not $SkipUi) {
    # Type-check before bundling. esbuild only transpiles, so without this a bad prop or a host API that moved
    # would sail through the build and only fail in the browser. CI can't run this (it needs a sibling cove
    # checkout for the host's types), which makes staging the last place to catch it.
    Write-Host "Type-checking extension UI..."
    Push-Location $repoRoot
    try {
        npm run typecheck
        if ($LASTEXITCODE -ne 0) { throw "npm run typecheck failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    Write-Host "Building extension UI bundles..."
    Push-Location $repoRoot
    try {
        npm run build:ui
        if ($LASTEXITCODE -ne 0) { throw "npm run build:ui failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Building Recommendations ($Configuration)..."
dotnet build (Join-Path $repoRoot "Recommendations.slnx") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

if (-not $SkipTests) {
    # The ranking maths has no visible failure mode — a regression in it just quietly reorders the feed — so the
    # unit tests run before anything reaches a live Cove, in the same spirit as the UI type-check above.
    Write-Host "Running unit tests..."
    dotnet test (Join-Path $repoRoot "tests\Recommendations.Tests\Recommendations.Tests.csproj") -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

foreach ($extension in $extensions) {
    $outputDir = Join-Path $repoRoot "extensions\$extension\bin\$Configuration\net10.0"
    $manifestPath = Join-Path $outputDir "extension.json"
    if (-not (Test-Path $manifestPath)) { throw "Missing manifest at $manifestPath" }

    $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($manifest.id)) { throw "Manifest at $manifestPath does not define an id" }

    $targetDir = Join-Path $DestinationRoot $manifest.id
    if (Test-Path $targetDir) { Remove-Item -Path $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -Path (Join-Path $outputDir "*") -Destination $targetDir -Recurse -Force

    Write-Host ("Staged {0} -> {1}" -f $manifest.id, $targetDir)
}

Write-Host "Local recommendation extensions are ready. Restart Cove to reload them if it is already running."
