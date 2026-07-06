# Lillypad Go — contributor & agent guide

A Pokémon creature-collector app that lives inside the Lillypad Toolkit phone shell
(`Phone/`). Walk Eorzea to find wild Pokémon, then battle or capture them. It ships the **151
Kanto Pokémon** with animated `gen5ani` spritesheets, real base stats, types, and level-up
learnsets.

**The species/move/learnset/encounter tables and the sprites are generated**, not hand-written —
see `tools/README.md`. Files ending in `.g.cs` (`MovesData.g.cs`, `PokedexData.g.cs`) and the
`Biome.cs` / `ZoneCatalog.cs` encounter tables come out of the generator; edit `tools/` and
re-run rather than editing them by hand. Everything else (screens, UI kit, battle rules) is
ordinary hand-written code.

This file is the map for extending the app. Read it before adding a screen or UI element so new
work matches the existing patterns.

## Build & verify

The .NET SDK is **not on PATH**. Use the AppData SDK (targets net10.0):

```
cd VideoSyncPrototype
& "C:\Users\<user>\AppData\Local\Microsoft\dotnet\dotnet.exe" build -c Debug -clp:ErrorsOnly
```

There is no in-repo way to see the rendered UI (it draws inside FFXIV via Dalamud/ImGui).
A clean build is the verification bar; keep it at **0 warnings, 0 errors**. When changing
visuals, only change *how* an element is drawn, not its coordinates, unless you are
deliberately re-laying-out a screen.

## Architecture at a glance

The app is one `partial class LillypadGoApp` split across files **by screen**, so you only ever
open the ~100–200 line file for the screen you're changing — never the whole app.

| Concern | File | Notes |
|---|---|---|
| Core / state / dispatch | `LillypadGoApp.cs` | `IPhoneApp` impl, all fields, `Draw`→`PaintView` switch, transitions. Start here to wire a new screen. |
| Starter screen | `LillypadGoApp.Starter.cs` | `DrawStarter`, `ChooseStarter`. |
| Map / overworld | `LillypadGoApp.Map.cs` | `DrawMap`, encounter card, `Engage`, party strip. |
| Battle screen | `LillypadGoApp.Battle.cs` | `DrawBattle`, playback/cue logic, displayed-state. |
| Battle visual FX | `LillypadGoApp.BattleFx.cs` | Damage popups, status FX, and **move animations**: `DrawMoveFx` plays `MoveAnims.For(move)` — a keyframe port of Showdown's animation (see below) — else the pattern fallback (Projectile/Beam/Contact/SelfBuff/Cloud). Wired from `ApplyCue` (`BeginMoveFx` on Player/WildAttack) → `DrawMoveFx`. |
| Move animations | `MoveAnim.cs`, `MoveAnimData.g.cs` | Keyframe runtime + data. Each move is a list of fx-sprite keyframes (showEffect) + bg flashes, ported from Showdown's CC0 `BattleMoveAnims` (exact for ~290 moves, synthesized default for the rest). `MoveAnims.Play` maps Showdown scene coords → screen (attacker/defender anchors + `CoordScale`, tune there) and tweens with Showdown easings. |
| Move animation data | `MoveVisuals.g.cs`, `MoveFxSprites.cs` | **Generated.** `MoveVisuals.For(name)` → `(pattern, fx sprite)` extracted from Showdown's CC0 `BattleMoveAnims`; `MoveFxSprites` streams the fx PNGs from `Assets/pokemon/fx/`. |
| Battle menus | `LillypadGoApp.BattleMenus.cs` | Message box, Fight/Bag/Team/Run menus, result, `FinishBattle`. |
| Team / storage | `LillypadGoApp.Team.cs` | `DrawTeam`, box↔party moves, `OpenDetail`. |
| Creature detail | `LillypadGoApp.Detail.cs` | `DrawDetail` (portrait, nickname, stats, moves). |
| Field guide | `LillypadGoApp.Dex.cs` | `DrawDex`. |
| Bag | `LillypadGoApp.Bag.cs` | `DrawBag`. |
| Shared view helpers | `LillypadGoApp.Shared.cs` | `DrawNavigation`, tooltips, `FitLabel`/`WrapText`, `Centered`/`CenteredAt`. |
| Shared UI kit | `LgUi.cs` | **Draw screens from these components, not raw squircles.** |
| Creature types | `MonsterSpecies.cs` | `MonsterSpecies`/`ArtSpec` structs + the `Dex` registry (dict, `Find`, `Add`/`LS` helpers). Species data itself is generated into `PokedexData.g.cs`. |
| Species data | `PokedexData.g.cs` | **Generated.** `Dex.Populate()` — 151 species: stats, types, catch rate, fallback `ArtSpec`, level-up learnset. |
| Moves | `MovesData.g.cs` (`Moves`) | **Generated.** Full Gen-1 pool. `Moves.M(id)`/`Find(name)`/`All`; `MoveDef`/enums live in `Move.cs`. |
| Elements & type chart | `Element.cs` (`Elements`) | 6 elements, opposed-pair effectiveness. |
| Biomes & fallback spawns | `Biome.cs` (`Biomes`) | Biome themes + non-ARR fallback encounters. |
| ARR zones & progression | `ZoneCatalog.cs` (`ArrZones`) | Territory level bands, exclusives, encounters. |
| Live creature | `MonsterInstance.cs` | Levels, XP, HP, PP, status, nickname. |
| Battle rules | `Battle.cs` | Turn resolution, capture/escape maths, message queue. |
| Encounters | `EncounterService.cs` | Step accrual → rolls a wild from the biome table. |
| Persistence | `LillypadGoState.cs` | `Plugin.LillypadGo`; saves to `lillypadgo.json`. Call `State.Save()` after mutations. |
| Art renderer | `MonsterArt.cs`, `PokemonSprites.cs`, `BiomeBackdrop.cs`, `BiomeBgTextures.cs` | `MonsterArt.Draw(dl, center, size, species, facing, pose, back?)` draws the animated sprite (via `PokemonSprites`, streamed from `Assets/pokemon/`), falling back to the procedural `ArtSpec` silhouette while a texture loads. `back:true` = player's own battler. `BiomeBackdrop.Draw` shows the imported Showdown arena photo for the biome (`BiomeBgTextures`, `Assets/pokemon/bg/<biome>.png`) with a contrast scrim, falling back to the procedural scene until loaded. |

Rendering is **immediate-mode**: each view's `DrawXxx(Rect content, PhoneTheme theme)` paints
straight to the ImGui draw list every frame from the passed `content` rect. There is no
retained widget tree. Derive every coordinate from `content` and multiply pixel sizes by
`ImGuiHelpers.GlobalScale`.

Saves store creatures by **species id** and **move name** (strings). Keep those stable —
renaming an id/name orphans existing saves (they're dropped on load, not crashed).

## The UI kit (`LgUi`)

All screens compose from these so the look stays consistent. Prefer extending `LgUi` over
hand-drawing a new panel style.

- `Card(dl, min, max, radius, scale, hovered?, sunken?)` — the standard elevated surface
  (shadow + gradient + hairline + top highlight). `sunken:true` for disabled/undiscovered.
- `Header(content, theme, accent, title, subtitle?, scale)` — page title bar.
- `Button(rect, label, fill, theme, enabled, sub?)` — primary tap target; returns `true` on
  click. Pass an element/accent colour as `fill`; ink colour is auto-picked for contrast.
- `Disclosure(rect, title, trailing, expanded, accent, theme, scale)` — collapsible section row.
- `Segmented(bounds, labels, selected, accent, theme, scale, ref indicator)` — iOS-style tab
  strip with a sliding pill. Keep the `indicator` float in a field (init to `-1f`). Returns
  the newly clicked index or `-1`.
- `Input(rect, id, ref text, maxLength, theme, scale)` — styled single-line field (matches the
  card language). Returns `true` on Enter. Note: `maxLength` is **`int`** (ImGui binding quirk).
- `EmptyState(center, icon, message, theme, scale)` — muted icon + line for empty lists.
- `HpBar` / `Meter` — gradient bars (HP shifts green→amber→red).
- `Scrollbar(track, offset, maxOffset, viewportFraction, accent, scale)` — passive list position.
- `Chip(dl, topLeft, element, scale, label?)` — small typed element tag.
- `LgUi.Interactive` — set `false` to freeze `Button`/`Segmented` input (used during transitions).

Colours come from `AppAccents.For(Id)` (the app accent) and `Elements.Color(element)`. Never
hard-code brand colour inside a component — take it as a parameter.

## Transitions

Switching views animates automatically: `Draw` diffs `view` against `lastDrawnView`, resets
`viewAnim`, and for ~180ms renders the incoming screen through
`SceneCompositor.DrawClipped` with a small upward ease (`Easing.EaseOutCubic`). The clip keeps
content off the status bar / home indicator and disables input during the slide. Adding a new
`View` needs nothing extra — just add it to the `View` enum and the `PaintView` switch.

## Recipes

### Add / change a creature or move
Species, moves, and learnsets are generated. Edit the generator in `tools/` (see
`tools/README.md`) and re-run, then rebuild — don't hand-edit `PokedexData.g.cs` /
`MovesData.g.cs` (regeneration overwrites them). To add sprites for a new species, drop its
frames through `build_sheets.py` so `Assets/pokemon/{front,back}/<id>.png` + `manifest.json`
gain an entry keyed by the species `id`. `StarterIds` in `LillypadGoApp.cs` (hand-written)
chooses the three starters.

### Add a biome / change spawns
The `Biome` enum, `Name`, `TerritoryMap`, and `BiomeBackdrop` cases are hand-written in
`Biome.cs`/`BiomeBackdrop.cs`, but the **spawn tables** (`Biomes.Tables`) and `ArrZones.All`
encounter lists are generated from `tools/emit.js` (via `tpl/Biome.cs.tpl` and
`tpl/ZoneCatalog.cs.tpl`). To retune which Pokémon spawn where, edit the zone/biome logic in
`emit.js` and re-run; to add a biome, add the enum/backdrop by hand and give `emit.js` a table.

### Add a new screen/view
1. In `LillypadGoApp.cs`: add a value to the `View` enum and a `case` in `PaintView` calling
   `DrawMyView(content, theme)`.
2. Create `LillypadGoApp.MyView.cs` — copy the header of any existing partial file (usings +
   `namespace …;` + `internal sealed partial class LillypadGoApp {`), then write `DrawMyView`
   inside it. It can freely use any field or helper (it's the same class).
3. In `DrawMyView`, start with `LgUi.Header(...)`, build the body from `LgUi` components, and end
   with `DrawNavigation(content, theme, scale)` if it should show the bottom tab bar.
4. Route to it by setting `view = View.MyView;` (the transition is automatic).

### Add a new UI component
Add it to `LgUi.cs` next to its peers, take colour/theme as parameters, honour `Interactive`
for any click handling, and keep pixel constants `* scale`. Document it with a one-line summary
in the list above.

## Conventions & gotchas
- Multiply every pixel size by `ImGuiHelpers.GlobalScale`.
- **Avoid overlaps.** Any text drawn from real data (names, nicknames, combined stat strings)
  must be width-constrained — wrap it in `FitLabel(text, maxWidth, style)` (ellipsizes) or
  `WrapText`. Un-clamped `Typography.Draw`/`DrawCentered` will spill past its card on long input.
- Views lay out with fixed `* scale` offsets from `content`, so they assume a **minimum content
  height**. On short windows / high Dalamud UI scale the content box can be shorter than the stack;
  guard bottom-anchored grids (clamp row height, `break` when a row would exceed the area) so they
  shrink or drop instead of inverting. Proportional layouts (fractions of `panel`/`content`) are
  inherently safe — prefer them for dense screens.
- Mutate then persist: after changing party/box/bag/stats, call `State.Save()`.
- `ImGui.InputText` maxLength is `int` here; passing `uint` binds the wrong overload.
- Don't reuse a stock `ImGui` widget mid-screen — wrap it in `LgUi` so it matches the card style.
- Battle text/animation is driven by `Battle.Log` (a message queue) and cues in `ApplyCue`; add
  new battle feedback by enqueuing messages with a `BattleCue`, not by poking the view directly.
