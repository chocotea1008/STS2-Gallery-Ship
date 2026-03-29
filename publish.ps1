param(
    [string]$Configuration = "Release"
)

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $projectDir)
$projectPath = Join-Path $projectDir "GalleryShip.csproj"
$buildDir = Join-Path $projectDir "bin\$Configuration\netcoreapp9.0"
$modsDir = Join-Path $repoRoot "mods\galleryship"
$stagingDir = Join-Path $repoRoot "_publish\galleryship_build_ready"

$running = Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "SlayTheSpire2.exe is running. Close the game before deploying the mod."
    exit 1
}

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

foreach ($targetDir in @($modsDir, $stagingDir)) {
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectDir "mod_manifest.json") -Destination (Join-Path $targetDir "mod_manifest.json") -Force
    Copy-Item -LiteralPath (Join-Path $projectDir "gallery_ship_button.png") -Destination (Join-Path $targetDir "gallery_ship_button.png") -Force
    Copy-Item -LiteralPath (Join-Path $projectDir "gallery_ship_frame_overlay.png") -Destination (Join-Path $targetDir "gallery_ship_frame_overlay.png") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "galleryship.dll") -Destination (Join-Path $targetDir "galleryship.dll") -Force
}

Write-Host "GalleryShip deployed to $modsDir and $stagingDir"
