# Lillypad Go data/asset generator

The 151 Kanto species, their move pool, learnsets, and encounter tables are **generated**
from [Pokémon Showdown](https://github.com/smogon/pokemon-showdown) data plus PokeAPI catch
rates. The animated sprites are built from Showdown's `gen5ani` GIFs. Do not hand-edit the
generated files — change the generator and re-run.

## Generated outputs (committed)

- `../MovesData.g.cs` — the `Moves` registry (ids match Showdown move ids). Effects now include
  accuracy-down (Sand Attack/Smokescreen), evasion-up (Double Team), and top-level confusion
  (Supersonic/Confuse Ray) in addition to the earlier stat/status/heal/recoil mappings.
- `../PokedexData.g.cs` — `Dex.Populate()` with all 151 species + level-up learnsets + the first
  Kanto evolution (`evolvesToId`, `evolveLevel`, `evolveMethod`); level-based ones auto-evolve.
- Item icons (`../../../../Assets/pokemon/items/*.png`) — the full Showdown item catalogue,
  cut from its icon sheet by `build_items.py` (see below). Gym badge emblems
  (`../../../../Assets/pokemon/badges/*.png`) are Showdown client type icons.
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

node extract.js             # -> gen1.json (species + moves, effect-mapped) + evolution info
node emit.js                # -> out/*.cs

# copy ONLY these two back — leave Biome.cs/ZoneCatalog.cs alone to keep the tuned
# encounter tables (out/ is excluded from the build via the csproj Compile Remove):
cp out/MovesData.g.cs out/PokedexData.g.cs ..

# sprites: download gen5ani front+back gifs into raw/{front,back}/<id>.gif, then:
python build_sheets.py      # -> sheets/{front,back}/<id>.png + sheets/manifest.json
```

### Item icons (build_items.py -> Assets/pokemon/items/)

Showdown packs every item icon into one 16-column sheet of 24x24 cells; an item's cell index
is its `spritenum` in the sim's `data/items.ts`. `build_items.py` parses those numbers, cuts
each cell, keys the flat `(1, 0, 0)` background to transparent, and writes
`Assets/pokemon/items/<showdown id>.png`. Our `ItemDef.Id`s **are** Showdown ids, so
`LgUi.ItemIcon` finds the art with no mapping table — adding an item to `Item.cs` with the
right id gives it an icon for free.

```
python build_items.py            # add missing icons (downloads + caches into data/)
python build_items.py --force    # re-cut every icon
python build_items.py --list     # report coverage, write nothing
```

Existing files are kept unless `--force`. That matters: Showdown's *sim* data has no medicine
at all (no potions, revives or status heals — they do nothing in a battle sim), so those nine
icons are higher-resolution PokeAPI art and must not be overwritten by 24px sheet cells. Same
reason `pokeball`/`greatball`/`ultraball` keep their PokeAPI versions even though the sheet
has them. TMs likewise have no sheet entry — `DrawTmRow` draws a numbered disc tinted by the
move's element instead.

### Move animations (trace_anims.js -> Assets/pokemon/moveanims.json)

Every move plays Showdown's real choreography. `trace_anims.js` **executes** each move's
`anim()` from `battle-animations-moves.js` (CC0-1.0 per its own header; the engine file is
MIT) against an instrumented mock of the `BattleScene`/`Sprite` API and records everything —
`showEffect` fx keyframes, attacker/defender sprite movement, `backgroundEffect` flashes,
`$bg` screen shakes, `scene.wait()` offsets — as data, in **both orientations** (player
attacking and wild attacking; the anims genuinely differ via `behind()`/`leftof()`).

```
curl -sSL https://play.pokemonshowdown.com/js/battle-animations.js       -o anim/battle-animations.js
curl -sSL https://play.pokemonshowdown.com/js/battle-animations-moves.js -o anim/battle-animations-moves.js
node trace_anims.js         # -> ../../../../Assets/pokemon/moveanims.json
```

The playback engine is `MoveAnim.cs` (`MoveAnims`): it replicates Showdown's `pos()`
perspective projection (near anchor 210,245 at z=0; far anchor 430,135 at z=200;
scale(z)=1.5-0.5·z/200), the per-CSS-property easings from `posT()` (ballistic arcs,
quad/swing curves), and the fade/explode finishers, then affine-maps the two scene anchors
onto the on-screen creature positions. Background flashes draw behind the creatures and fx
draw above them, matching Showdown's layer order. `$attacker`/`$defender` pseudo-sprites
(Double Team clones etc.) resolve to the creatures' current spritesheet frames.

Fx sprite art lives in `Assets/pokemon/fx/` (download missing names reported by the tracer
from `play.pokemonshowdown.com/fx/<name>.png`; a few moves also use the jpg/png backdrop
images referenced in `bg` entries).

- **`MoveVisuals.g.cs`** (`extract_anims.js` + `emit_anims.js`) — a lightweight per-move
  (pattern, fx-sprite) map used by `DrawMoveFx`'s pattern fallback, which only plays if
  `moveanims.json` is missing or a move can't be traced.

## Notes / fidelity limits
- Move effects are collapsed to the single-effect `MoveEffect` enum (no sleep/multi-hit/weather/
  two-turn modelling); fixed/variable-power moves fall back to 50 power so they still deal damage.
  `Battle.MovePower` then re-derives the real power for the variable-power moves it knows by name
  (Flail, Electro Ball, Gyro Ball, Low Kick, Grass Knot, Heavy Slam, Heat Crash, ...), so those do
  *not* stay at 50. The weight-scaled four read `MonsterSpecies.WeightKg`, emitted from the
  pokedex's `weightkg`.
- Learnsets use each move's newest-generation level-up entry. Catch rates come from PokeAPI.
- A few legendaries (Mewtwo/Mew/Zapdos) may not fall into any zone's level+biome window; they
  exist in the Dex but can be rare/unspawned. Tune `emit.js` zone logic to place them explicitly.
