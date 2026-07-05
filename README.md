# Lillypad Toolkit

Dalamud dev plugin for starting a shared Watch2Gether room from inside FFXIV and displaying it on an in-world TV.

Use `/lilly` or `/pad` in game to open the plugin window.

## Features

- Create a temporary Watch2Gether room from a pasted video URL.
- Share the room code/link through the configured chat channel.
- Join a shared room on an in-world TV or in the desktop browser.
- Native world-space TV rendering with depth, so players can stand in front of it.
- TV size, height, distance, stretch, and placement controls.
- Style tab with optional frame skins, ambient glow, and cinema presets.
- Search nearby players, flag them on the map, examine them, view adventurer plates, or open Lodestone searches.
- Show configurable friend, Free Company, and nearby-player markers on the minimap and main map.
- Remap Penumbra dance/emote mods from locked emotes onto emotes you own.
- Searchable emote presets, automatic unlocked-carrier selection, and duo/group position alignment.
- No constant playback-sync spam through chat. Watch2Gether handles play, pause, seek, and room state.

## Requirements

- FFXIV with XIVLauncher/Dalamud.
- Dalamud API 15.
- Microsoft Edge WebView2 Runtime.
- .NET 10 SDK to build from source.
- A free Watch2Gether API key if you want to create rooms.

Joining a room does not require a Watch2Gether API key.

## Friend Testing From A Zip

1. Download and extract `VideoSyncPrototype-friend-test.zip`.
2. In game, open `/xlsettings`.
3. Go to `Experimental`.
4. Add the extracted `VideoSyncPrototype.dll` as a dev plugin location.
5. Open `/xldev` or `/xlplugins`, then enable `Lillypad Toolkit`.
6. Open `/lilly` or `/pad`.

Keep the `OverlayPlayer` folder next to `VideoSyncPrototype.dll`. The TV uses that helper app to render the browser.

## Build From Source

From the repository root:

```powershell
dotnet build .\VideoSyncPrototype.csproj -c Debug
dotnet publish .\OverlayPlayer\OverlayPlayer.csproj -c Debug -r win-x64 -o .\bin\Debug\OverlayPlayer --self-contained true
```

If this workspace has the local SDK folder from development, this also works from the parent folder:

```powershell
.\.dotnet\dotnet.exe build .\VideoSyncPrototype\VideoSyncPrototype.csproj -c Debug
.\.dotnet\dotnet.exe publish .\VideoSyncPrototype\OverlayPlayer\OverlayPlayer.csproj -c Debug -r win-x64 -o .\VideoSyncPrototype\bin\Debug\OverlayPlayer --self-contained true
```

Then add this dev plugin DLL in Dalamud:

```text
VideoSyncPrototype\bin\Debug\VideoSyncPrototype.dll
```

## Package A Test Zip

Run:

```powershell
.\scripts\package-release.ps1
```

The zip will be written to:

```text
artifacts\VideoSyncPrototype.zip
```

That package includes:

- `VideoSyncPrototype.dll`
- `VideoSyncPrototype.deps.json`
- `VideoSyncPrototype.json`
- `OverlayPlayer\`

## Custom Repository Setup

Dalamud custom plugin repositories use a public JSON file, usually named `pluginmaster.json`. Users paste that raw JSON URL into:

```text
/xlsettings > Experimental > Custom Plugin Repositories
```

To generate a release zip and `pluginmaster.json` for GitHub Releases:

```powershell
.\scripts\package-release.ps1 `
  -Owner YOUR_GITHUB_NAME `
  -Repo YOUR_REPO_NAME `
  -Tag v0.0.0.1 `
  -WritePluginMaster
```

This writes:

```text
artifacts\VideoSyncPrototype.zip
artifacts\pluginmaster.json
pluginmaster.json
```

Commit and push `pluginmaster.json` to the repository root. Then create a GitHub Release with the matching tag, for example `v0.0.0.1`, and upload:

```text
artifacts\VideoSyncPrototype.zip
```

Friends can then add this custom repo URL in Dalamud:

```text
https://raw.githubusercontent.com/YOUR_GITHUB_NAME/YOUR_REPO_NAME/main/pluginmaster.json
```

The `pluginmaster.example.json` file is only a reference. The real generated `pluginmaster.json` is what Dalamud should use.

## Notes

This is a prototype/dev plugin intended for private testing. It is not an official Dalamud repository plugin yet.

Watch2Gether temporary rooms are external to the plugin and may expire according to Watch2Gether's room rules.
