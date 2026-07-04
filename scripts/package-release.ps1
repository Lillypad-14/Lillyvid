param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Owner = "",
    [string]$Repo = "",
    [string]$Tag = "",
    [string]$AssetName = "VideoSyncPrototype.zip",
    [switch]$WritePluginMaster
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$dotnet = "dotnet"
$localDotnet = Join-Path (Split-Path $repoRoot -Parent) ".dotnet\dotnet.exe"
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
}

$artifactRoot = Join-Path $repoRoot "artifacts"
$packageRoot = Join-Path $artifactRoot "VideoSyncPrototype"
$overlayOut = Join-Path $packageRoot "OverlayPlayer"
$zipPath = Join-Path $artifactRoot $AssetName

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
New-Item -ItemType Directory -Force -Path $overlayOut | Out-Null

& $dotnet build (Join-Path $repoRoot "VideoSyncPrototype.csproj") -c $Configuration
& $dotnet publish (Join-Path $repoRoot "OverlayPlayer\OverlayPlayer.csproj") `
    -c $Configuration `
    -r $Runtime `
    -o $overlayOut `
    --self-contained true

Copy-Item -LiteralPath (Join-Path $repoRoot "bin\$Configuration\VideoSyncPrototype.dll") -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "bin\$Configuration\VideoSyncPrototype.deps.json") -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "bin\$Configuration\VideoSyncPrototype.json") -Destination $packageRoot -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

if ($WritePluginMaster) {
    if ([string]::IsNullOrWhiteSpace($Owner) -or [string]::IsNullOrWhiteSpace($Repo) -or [string]::IsNullOrWhiteSpace($Tag)) {
        throw "Owner, Repo, and Tag are required when using -WritePluginMaster."
    }

    $project = [xml](Get-Content -LiteralPath (Join-Path $repoRoot "VideoSyncPrototype.csproj"))
    $manifest = Get-Content -LiteralPath (Join-Path $repoRoot "VideoSyncPrototype.json") | ConvertFrom-Json
    $downloadUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$AssetName"
    $repoUrl = "https://github.com/$Owner/$Repo"
    $lastUpdate = [int][double]::Parse((Get-Date -UFormat %s), [Globalization.CultureInfo]::InvariantCulture)

    $entry = [ordered]@{
        Author = $manifest.Author
        Name = $manifest.Name
        InternalName = "VideoSyncPrototype"
        AssemblyVersion = $project.Project.PropertyGroup.Version
        Description = $manifest.Description
        Punchline = $manifest.Punchline
        ApplicableVersion = $manifest.ApplicableVersion
        DalamudApiLevel = 15
        RepoUrl = $repoUrl
        Tags = $manifest.Tags
        IsHide = $false
        IsTestingExclusive = $false
        DownloadCount = 0
        DownloadLinkInstall = $downloadUrl
        DownloadLinkUpdate = $downloadUrl
        DownloadLinkTesting = $null
        LastUpdate = $lastUpdate
    }

    $pluginMasterJson = "[`n" + ($entry | ConvertTo-Json -Depth 8) + "`n]"
    $artifactPluginMaster = Join-Path $artifactRoot "pluginmaster.json"
    $rootPluginMaster = Join-Path $repoRoot "pluginmaster.json"
    Set-Content -LiteralPath $artifactPluginMaster -Value $pluginMasterJson -Encoding UTF8
    Set-Content -LiteralPath $rootPluginMaster -Value $pluginMasterJson -Encoding UTF8
    Write-Host "Plugin master:"
    Write-Host $rootPluginMaster
}

Write-Host "Packaged:"
Write-Host $zipPath
