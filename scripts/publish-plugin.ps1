param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Remote = "origin",
    [string]$Branch = "main",
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VideoSyncPrototype.csproj"
$pluginMasterPath = Join-Path $repoRoot "pluginmaster.json"
$releaseDir = Join-Path $repoRoot "release"
$dotnet = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

function Get-ProjectVersion {
    $project = [xml](Get-Content -LiteralPath $projectPath)
    return [string]$project.Project.PropertyGroup.Version
}

function Set-ProjectVersion([string]$newVersion) {
    $project = [xml](Get-Content -LiteralPath $projectPath)
    $project.Project.PropertyGroup.Version = $newVersion
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($projectPath, $settings)
    $project.Save($writer)
    $writer.Close()
}

function Get-NextPatchVersion([string]$current) {
    $parts = $current.Split(".") | ForEach-Object { [int]$_ }
    if ($parts.Count -ne 4) {
        throw "Expected a four-part version like 7.0.3.0, got '$current'."
    }

    $parts[2] += 1
    $parts[3] = 0
    return ($parts -join ".")
}

function Set-PluginMaster([string]$newVersion) {
    $json = Get-Content -LiteralPath $pluginMasterPath -Raw | ConvertFrom-Json
    $entry = $json[0]
    $downloadUrl = "https://raw.githubusercontent.com/Lillypad-14/Lillyvid/main/release/VideoSyncPrototype-$newVersion.zip"
    $entry.AssemblyVersion = $newVersion
    $entry.DownloadLinkInstall = $downloadUrl
    $entry.DownloadLinkUpdate = $downloadUrl
    $entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $text = "[`n" + ($entry | ConvertTo-Json -Depth 8) + "`n]"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($pluginMasterPath, $text, $utf8NoBom)
}

function New-ReleaseZip([string]$newVersion) {
    & $dotnet build $projectPath -c $Configuration -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }

    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $zipPath = Join-Path $releaseDir "VideoSyncPrototype-$newVersion.zip"
    $latestPath = Join-Path $releaseDir "VideoSyncPrototype.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $outputRoot = Join-Path $repoRoot "bin\$Configuration"
    $items = Get-ChildItem -LiteralPath $outputRoot -Force | Where-Object { $_.Name -ne "VideoSyncPrototype" }
    Compress-Archive -Path $items.FullName -DestinationPath $zipPath -Force
    Copy-Item -LiteralPath $zipPath -Destination $latestPath -Force

    $expectedAssets = (Get-ChildItem -LiteralPath (Join-Path $repoRoot "Assets\pokemon") -Recurse -File | Measure-Object).Count
    $zipAssets = (tar -tf $zipPath | Select-String "^Assets/pokemon/" | Measure-Object).Count
    if ($expectedAssets -le 0 -or $zipAssets -ne $expectedAssets) {
        throw "Release zip asset verification failed. Expected $expectedAssets Assets/pokemon files, found $zipAssets in $zipPath."
    }

    foreach ($required in @(
        "Assets/pokemon/manifest.json",
        "Assets/pokemon/moveanims.json",
        "Assets/pokemon/items/pokeball.png",
        "Assets/pokemon/badges/Water.png",
        "OverlayPlayer/OverlayPlayer.exe",
        "VideoSyncPrototype.dll",
        "VideoSyncPrototype.json"
    )) {
        if (-not (tar -tf $zipPath | Select-String ([regex]::Escape($required)))) {
            throw "Release zip is missing required file: $required"
        }
    }

    Write-Host "Built $zipPath"
    Write-Host "Verified Lillypad Go assets in zip: $zipAssets"
}

$currentVersion = Get-ProjectVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-NextPatchVersion $currentVersion
}

Set-ProjectVersion $Version
Set-PluginMaster $Version
New-ReleaseZip $Version

& git add -A
& git add -f "release\VideoSyncPrototype-$Version.zip" "release\VideoSyncPrototype.zip"
& git commit -m "Release Lillypad Toolkit $($Version.TrimEnd('.0'))"
& git tag "v$Version"

if (-not $SkipPush) {
    & git push $Remote $Branch --tags
}

Write-Host "Release ready: $Version"
