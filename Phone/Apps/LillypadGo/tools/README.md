# Lillypad Go data/asset generator

The 151 Kanto species, their move pool, learnsets, and encounter tables are **generated**
from [Pokémon Showdown](https://github.com/smogon/pokemon-showdown) data plus PokeAPI catch
rates. The animated sprites are built from Showdown's `gen5ani` GIFs. Do not hand-edit the
generated files — change the generator and re-run.

## Generated outputs (committed)

- `../MovesData.g.cs` — the `Moves` registry (ids match Showdown move ids).
- `../PokedexData.g.cs` — `Dex.Populate()` with all 151 species + level-up learnsets.
- `../Biome.cs`, `../ZoneCatalog.cs` — Kanto encounter tables (rendered from `tpl/*.tpl`).
- `../../../../Assets/pokemon/{front,back}/<id>.png` + `manifest.json` — animated spritesheets
  (one horizontal PNG strip per creature; `manifest.json` holds frame count + per-frame delays).

`<id>` is the Showdown species id (the pokedex key), e.g. `nidoranf`, `mrmime`, `farfetchd`.

## Regenerate

Requires Node, Python 3 + Pillow, and network access. From this folder:

```
npm i typescript            # once
mkdir -p data
curl -sSL https://raw.githubusercontent.com/smogon/pokemon-showdown/master/data/pokedex.ts   -o data/pokedex.ts
curl -sSL https://raw.githubusercontent.com/smogon/pokemon-showdown/master/data/moves.ts      -o data/moves.ts
curl -sSL https://raw.githubusercontent.com/smogon/pokemon-showdown/master/data/learnsets.ts  -o data/learnsets.ts
curl -sSL https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/pokemon_species.csv -o data/species.csv

node extract.js             # -> gen1.json (species + moves, effect-mapped) + ids.txt
node emit.js                # -> out/*.cs (copy into the LillypadGo folder)

# sprites: download gen5ani front+back gifs into raw/{front,back}/<id>.gif, then:
python build_sheets.py      # -> sheets/{front,back}/<id>.png + sheets/manifest.json
```

### Move animations

```
curl -sSL https://play.pokemonshowdown.com/js/battle-animations-moves.js -o anim/anims.js
node extract_anims.js       # -> movevisuals.json (move -> {pattern, fx sprite})
node emit_anims.js          # -> out/MoveVisuals.g.cs (copy into the LillypadGo folder)
# download each fx sprite in movevisuals.json `sprites` from play.pokemonshowdown.com/fx/<name>.png
# into Assets/pokemon/fx/<name>.png
```

`anims.js` is Showdown's `BattleMoveAnims`, which is **CC0-1.0** (public domain) per its own header.

There are two layers:
- **`MoveVisuals.g.cs`** (`extract_anims.js` + `emit_anims.js`) — a lightweight per-move
  (pattern, fx-sprite) map used by the pattern fallback in `DrawMoveFx`.
- **`MoveAnimData.g.cs`** (`extract_keyframes.js` + `emit_keyframes.js`) — the real thing: the
  actual keyframes (`showEffect` sprite + start/end x/y/z/scale/opacity/time + easing, and
  `backgroundEffect` flashes) extracted **as data** and replayed by the `MoveAnims` tween runtime
  in `MoveAnim.cs`. ~290 moves get Showdown's exact choreography; the rest get a synthesized
  default from their pattern. Requires `graphics.js` too (shared `BattleOtherAnims`):

```
curl -sSL https://play.pokemonshowdown.com/data/graphics.js -o anim/graphics.js
node extract_keyframes.js   # -> keyframes.json (+ sprite list)
# download each fx sprite in keyframes.json `sprites` into fx/, record dims (build_sheets-style),
node emit_keyframes.js      # -> out/MoveAnimData.g.cs
```

Only keyframe DATA (coordinates/timings) is read; the animation code is not transpiled. Tuning:
`MoveAnims.CoordScale` in `MoveAnim.cs` scales Showdown scene units → screen pixels.

## Notes / fidelity limits

- Showdown's *move animations* are a bespoke JS/CSS engine in their web client and are **not**
  ported. Battle feedback uses the existing procedural FX (`LillypadGoApp.BattleFx.cs`) plus the
  sprite lunge/hurt/faint poses.
- Move effects are collapsed to the single-effect `MoveEffect` enum (no sleep/multi-hit/weather/
  two-turn modelling); fixed/variable-power moves fall back to 50 power so they still deal damage.
- Learnsets use each move's newest-generation level-up entry. Catch rates come from PokeAPI.
- A few legendaries (Mewtwo/Mew/Zapdos) may not fall into any zone's level+biome window; they
  exist in the Dex but can be rare/unspawned. Tune `emit.js` zone logic to place them explicitly.
