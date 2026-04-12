$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectPath = Join-Path $projectRoot "GalleryShip.csproj"
$buildDir = Join-Path $projectRoot "bin\Release\netcoreapp9.0"
$buildDll = Join-Path $projectRoot "bin\Release\netcoreapp9.0\galleryship.dll"
$modsRoot = "D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\galleryship"
$modsDll = Join-Path $modsRoot "galleryship.dll"

Write-Host "[GalleryShip] Building release DLL..."
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) {
	throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $buildDll)) {
	throw "Build output not found: $buildDll"
}

New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null

Write-Host "[GalleryShip] Copying DLL to mods folder..."
Copy-Item -LiteralPath $buildDll -Destination $modsDll -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "mod_manifest.json") -Destination (Join-Path $modsRoot "mod_manifest.json") -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "gallery_ship_button.png") -Destination (Join-Path $modsRoot "gallery_ship_button.png") -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "gallery_ship_player_badge.webp") -Destination (Join-Path $modsRoot "gallery_ship_player_badge.webp") -Force
foreach ($staleFile in @("System.Drawing.Common.dll", "Microsoft.Win32.SystemEvents.dll")) {
	$stalePath = Join-Path $modsRoot $staleFile
	if (Test-Path -LiteralPath $stalePath) {
		Remove-Item -LiteralPath $stalePath -Force
	}
}
$depsPath = Join-Path $modsRoot "galleryship.deps.json"
if (Test-Path -LiteralPath $depsPath) {
	Remove-Item -LiteralPath $depsPath -Force
}

$buildHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildDll).Hash
$modsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $modsDll).Hash

if ($buildHash -ne $modsHash) {
	throw "Hash mismatch after copy. build=$buildHash mods=$modsHash"
}

Write-Host "[GalleryShip] Deploy complete."
Write-Host "[GalleryShip] Build DLL : $buildDll"
Write-Host "[GalleryShip] Mods DLL  : $modsDll"
Write-Host "[GalleryShip] SHA256    : $buildHash"
