"""Slice every item icon out of Pokemon Showdown's itemicons sheet.

Showdown packs all item icons into one 16-column sheet of 24x24 cells; each item's
cell index is its `spritenum` in the sim's `data/items.ts` (the client's
`Dex.getItemIcon` does `left = num % 16 * 24; top = num // 16 * 24`). The sheet has
no alpha channel -- the background is the flat key colour (1, 0, 0).

Writes `Assets/pokemon/items/<showdown id>.png`. Our `ItemDef.Id`s already use
Showdown ids, so `LgUi.ItemIcon` picks these up with no further mapping.

Existing files are kept unless --force: the dozen medicine/ball icons already in
that folder are higher-resolution PokeAPI art and should not be downgraded to the
24px sheet cells. (Showdown's sim data has no medicine items at all -- no potions,
revives or status heals -- so those must keep coming from PokeAPI.)

Usage (from this folder, needs network unless --sheet/--items point at local files):
    python build_items.py            # add missing icons
    python build_items.py --force    # re-cut every icon
    python build_items.py --list     # report coverage, write nothing
"""

import argparse
import os
import re
import sys
import urllib.request

SHEET_URL = "https://play.pokemonshowdown.com/sprites/itemicons-sheet.png"
ITEMS_URL = "https://raw.githubusercontent.com/smogon/pokemon-showdown/master/data/items.ts"

HERE = os.path.dirname(os.path.abspath(__file__))
DEST = os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", "Assets", "pokemon", "items"))

CELL = 24
COLUMNS = 16
KEY_COLOR = (1, 0, 0)  # the sheet's flat background, keyed to transparent

# `\tid: {` ... `\t\tspritenum: N,` -- the first spritenum after an id belongs to it.
ENTRY_RE = re.compile(r"^\t(\w+): \{$", re.MULTILINE)
SPRITENUM_RE = re.compile(r"^\t\tspritenum: (\d+),$", re.MULTILINE)


def fetch(url, cache_name):
    """Download `url` once into this folder, then reuse the cached copy."""
    path = os.path.join(HERE, "data", cache_name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if not os.path.exists(path):
        print(f"downloading {url}")
        urllib.request.urlretrieve(url, path)
    return path


def parse_spritenums(items_ts):
    """{ showdown item id: spritenum } for every item that has an icon."""
    with open(items_ts, encoding="utf-8") as handle:
        text = handle.read()

    out = {}
    entries = list(ENTRY_RE.finditer(text))
    for i, match in enumerate(entries):
        end = entries[i + 1].start() if i + 1 < len(entries) else len(text)
        sprite = SPRITENUM_RE.search(text, match.end(), end)
        if sprite:
            out[match.group(1)] = int(sprite.group(1))
    return out


def key_out(cell):
    """Replace the sheet's flat background with transparency."""
    cell = cell.convert("RGBA")
    pixels = cell.load()
    for y in range(cell.height):
        for x in range(cell.width):
            r, g, b, _ = pixels[x, y]
            if (r, g, b) == KEY_COLOR:
                pixels[x, y] = (r, g, b, 0)
    return cell


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--force", action="store_true", help="overwrite existing icons")
    parser.add_argument("--list", action="store_true", help="report coverage, write nothing")
    parser.add_argument("--sheet", help="local itemicons-sheet.png instead of downloading")
    parser.add_argument("--items", help="local items.ts instead of downloading")
    args = parser.parse_args()

    try:
        from PIL import Image
    except ImportError:
        sys.exit("Pillow is required: pip install Pillow")

    items_ts = args.items or fetch(ITEMS_URL, "items.ts")
    spritenums = parse_spritenums(items_ts)
    if not spritenums:
        sys.exit(f"parsed no items out of {items_ts} -- has the file format changed?")

    if args.list:
        have = {f[:-4] for f in os.listdir(DEST) if f.endswith(".png")}
        missing = sorted(set(spritenums) - have)
        extra = sorted(have - set(spritenums))
        print(f"{len(spritenums)} items in Showdown data, {len(have)} icons on disk")
        print(f"{len(missing)} would be added: {', '.join(missing[:8])}{' ...' if len(missing) > 8 else ''}")
        print(f"{len(extra)} on disk with no Showdown entry (PokeAPI medicine): {', '.join(extra)}")
        return

    sheet_path = args.sheet or fetch(SHEET_URL, "itemicons-sheet.png")
    sheet = Image.open(sheet_path).convert("RGB")
    capacity = (sheet.width // CELL) * (sheet.height // CELL)
    os.makedirs(DEST, exist_ok=True)

    written = skipped = missing = 0
    for item_id, num in sorted(spritenums.items()):
        if num >= capacity:
            print(f"  ! {item_id}: spritenum {num} past the end of the sheet")
            missing += 1
            continue

        out_path = os.path.join(DEST, f"{item_id}.png")
        if os.path.exists(out_path) and not args.force:
            skipped += 1
            continue

        left, top = (num % COLUMNS) * CELL, (num // COLUMNS) * CELL
        cell = key_out(sheet.crop((left, top, left + CELL, top + CELL)))
        if not cell.getbbox():  # an empty sheet cell means Showdown has no art for it
            print(f"  ! {item_id}: sheet cell {num} is blank")
            missing += 1
            continue

        cell.save(out_path)
        written += 1

    print(f"{written} written, {skipped} kept (already present), {missing} unavailable -> {DEST}")


if __name__ == "__main__":
    main()
