$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$stageDir = Join-Path $root "publish\app"
$releaseDir = Join-Path $root "releases"
$payloadZip = Join-Path $root "src\NovaDownloader.Installer\payload.zip"

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Write-Host "1/4 Publishing NovaDownloader.UI (self-contained shared runtime)..."
dotnet publish "$root\src\NovaDownloader.UI\NovaDownloader.UI.csproj" `
    -c Release -r win-x64 --self-contained true -o "$stageDir"

Write-Host "2/4 Publishing NovaDownloader.ExtensionHost (sharing runtime)..."
dotnet publish "$root\src\NovaDownloader.ExtensionHost\NovaDownloader.ExtensionHost.csproj" `
    -c Release -r win-x64 --self-contained false -o "$stageDir"

# Remove .pdb, xml doc, and non-English satellite resource folders to keep installer compact
Get-ChildItem $stageDir -Include "*.pdb","*.xml" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
@("cs","de","es","fr","it","ja","ko","pl","pt-BR","ru","tr","zh-Hans","zh-Hant") | ForEach-Object {
    $loc = Join-Path $stageDir $_
    if (Test-Path $loc) { Remove-Item $loc -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "3/4 Packaging payload.zip..."
Compress-Archive -Path "$stageDir\*" -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "4/4 Publishing single-file NovaDownloader-Setup.exe..."
dotnet publish "$root\src\NovaDownloader.Installer\NovaDownloader.Installer.csproj" `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$releaseDir"

Get-ChildItem $releaseDir -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue
$sizeMb = [math]::Round((Get-Item "$releaseDir\NovaDownloader-Setup.exe").Length / 1MB, 1)
Write-Host "DONE -> $releaseDir\NovaDownloader-Setup.exe ($sizeMb MB)"
