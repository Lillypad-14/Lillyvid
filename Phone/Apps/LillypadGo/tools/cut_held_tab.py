"""Cut the Key Items key out of the Bag mockup and recolour it to held-item violet.

Matches the other roster/tab_*.png crops: hard binary alpha, drop shadow removed, no resize.
The mockup's cream tab face is warm (r-b ~ +44) and light, while the key is either bluish
(b > r) or near-black outline -- so "warm AND light" keys out the face and its shadow together.
"""
import colorsys
import numpy as np
from PIL import Image

SRC = r"C:\Users\clemm\Desktop\Fable5\Ideas\UI Update\Bag.png"
DST = r"C:\Users\clemm\Desktop\Fable5\VideoSyncPrototype\Assets\pokemon\roster\tab_held.png"
BOX = (728, 164, 788, 225)          # generous box around the Key Items glyph
VIOLET_HUE = 0.74                    # LgUi.ItemTint(ItemCategory.HeldItem) = (0.68, 0.48, 0.90)

rgb = np.array(Image.open(SRC).convert("RGB").crop(BOX)).astype(int)
r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
luma = 0.299 * r + 0.587 * g + 0.114 * b

background = ((r - b) > 20) & (luma > 120)
fg = ~background

# Drop specks: keep only pixels with at least two foreground neighbours.
pad = np.pad(fg, 1, constant_values=False)
neighbours = sum(
    pad[1 + dy: 1 + dy + fg.shape[0], 1 + dx: 1 + dx + fg.shape[1]]
    for dy in (-1, 0, 1) for dx in (-1, 0, 1) if (dy, dx) != (0, 0)
)
fg &= neighbours >= 2

ys, xs = np.nonzero(fg)
y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
rgb, fg = rgb[y0:y1, x0:x1], fg[y0:y1, x0:x1]

# Rotate every saturated pixel's hue to violet, preserving the painter's shading (S and V).
out = np.zeros((*fg.shape, 4), dtype=np.uint8)
for y in range(fg.shape[0]):
    for x in range(fg.shape[1]):
        if not fg[y, x]:
            continue
        h, s, v = colorsys.rgb_to_hsv(*(rgb[y, x] / 255.0))
        if s > 0.08:
            h = VIOLET_HUE
        nr, ng, nb = colorsys.hsv_to_rgb(h, s, v)
        out[y, x] = (round(nr * 255), round(ng * 255), round(nb * 255), 255)

Image.fromarray(out, "RGBA").save(DST)
print(f"wrote {DST}  size={out.shape[1]}x{out.shape[0]}  opaque={int(100 * fg.mean())}%")
