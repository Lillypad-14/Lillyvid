#!/usr/bin/env python3
# Converts each animated GIF into a horizontal PNG spritesheet + a manifest entry.
# All frames of one animation are cropped to a shared bounding box so the sprite stays
# aligned and fills the cell consistently across species.
import json, os, sys
from PIL import Image, ImageSequence

ROOT = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(ROOT, "raw")
OUT = os.path.join(ROOT, "sheets")
IDS = [l.strip() for l in open(os.path.join(ROOT, "ids.txt")) if l.strip()]

def load_frames(path):
    im = Image.open(path)
    frames, delays = [], []
    for fr in ImageSequence.Iterator(im):
        delays.append(int(fr.info.get("duration", 100)) or 100)
        frames.append(fr.convert("RGBA"))
    return frames, delays

def union_bbox(frames):
    box = None
    for fr in frames:
        b = fr.getbbox()
        if b is None:
            continue
        box = b if box is None else (min(box[0], b[0]), min(box[1], b[1]),
                                     max(box[2], b[2]), max(box[3], b[3]))
    return box

def build(path):
    frames, delays = load_frames(path)
    box = union_bbox(frames) or (0, 0, frames[0].width, frames[0].height)
    frames = [fr.crop(box) for fr in frames]
    w, h = frames[0].size
    sheet = Image.new("RGBA", (w * len(frames), h), (0, 0, 0, 0))
    for i, fr in enumerate(frames):
        sheet.paste(fr, (i * w, 0))
    return sheet, {"frames": len(frames), "w": w, "h": h, "delays": delays}

manifest = {}
for side in ("front", "back"):
    os.makedirs(os.path.join(OUT, side), exist_ok=True)

for n, mid in enumerate(IDS, 1):
    entry = {}
    for side in ("front", "back"):
        src = None
        for ext in (".gif", ".png"):
            p = os.path.join(RAW, side, mid + ext)
            if os.path.exists(p):
                src = p; break
        if not src:
            continue
        sheet, meta = build(src)
        sheet.save(os.path.join(OUT, side, mid + ".png"), optimize=True)
        entry[side] = meta
    manifest[mid] = entry
    if n % 30 == 0:
        print(f"  {n}/{len(IDS)} ...")

json.dump(manifest, open(os.path.join(OUT, "manifest.json"), "w"), separators=(",", ":"))

# Summary
tot = sum(len(os.listdir(os.path.join(OUT, s))) for s in ("front", "back"))
frame_counts = [m["front"]["frames"] for m in manifest.values() if "front" in m]
size_mb = sum(os.path.getsize(os.path.join(dp, f))
              for dp, _, fs in os.walk(OUT) for f in fs) / 1e6
print(f"sheets written: {tot} pngs, manifest: {len(manifest)} entries")
print(f"front frame counts: min={min(frame_counts)} max={max(frame_counts)} "
      f"avg={sum(frame_counts)/len(frame_counts):.1f}")
print(f"total asset size: {size_mb:.1f} MB")
print("sample bulbasaur:", manifest["bulbasaur"])
