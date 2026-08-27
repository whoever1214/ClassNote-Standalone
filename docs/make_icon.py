# -*- coding: utf-8 -*-
"""
ClassNote client icon generator.
Flat modern notebook-themed icon packaged as a multi-resolution .ico.

Design: rounded-square blue (#1976D2 family) tile with an open notebook
carrying a small audio "note-wave" glyph, and a pen lying along the lower
right corner. A soft sheen and a highlight star give it a friendly finish.
"""
import math
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ICO_PATH = os.path.join(HERE, "..", "ClassNote", "app.ico")
PNG_256 = os.path.join(HERE, "classnote-icon-256.png")
PNG_512 = os.path.join(HERE, "classnote-icon-512.png")

# master canvas (supersampled, then downscaled for anti-aliasing)
SIZE = 2048

# ---- palette ----------------------------------------------------------------
ACCENT      = (25, 118, 210, 255)   # #1976D2
ACCENT_DARK = (11, 62, 146, 255)    # #0B3E92
PAPER       = (255, 255, 255, 255)
PAPER_ALT   = (238, 242, 249, 255)  # slightly tinted right page
PENAME      = (255, 160, 0, 255)    # orange
INK         = (40, 54, 84, 255)     # dark ink
RULE        = (203, 216, 235, 255)  # ruled line
HIGHLIGHT   = (255, 213, 79, 255)   # star


def px(v):
    return int(round(v * SIZE / 100))


def rounded_rect(d, x0, y0, x1, y1, radius, fill, outline=None, width=0):
    d.rounded_rectangle((x0, y0, x1, y1), radius=radius,
                        fill=fill, outline=outline, width=width)


img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# ---- tile background --------------------------------------------------------
T = px(8)                 # tile inset
TS = px(92)               # tile span (so the whole square is 8..100 with margin)
rounded_rect(d, T, T, T + TS, T + TS, radius=px(21), fill=ACCENT_DARK)
rounded_rect(d, T, T, T + TS, T + TS, radius=px(21), fill=ACCENT)

# soft top sheen
d.ellipse((px(16), px(-24), px(84), px(30)), fill=(255, 255, 255, 34))

# ---- notebook ----------------------------------------------------------------
NB_W = px(58)
NB_H = px(66)
NB_X = px(21)
NB_Y = px(17)
RAD  = px(6)

# drop shadow
d.rounded_rectangle(
    (NB_X + px(2), NB_Y + px(4), NB_X + NB_W + px(3), NB_Y + NB_H + px(4)),
    radius=RAD, fill=(0, 0, 0, 70))

# left page
rounded_rect(d, NB_X, NB_Y, NB_X + NB_W // 2, NB_Y + NB_H, RAD, PAPER)
# right page
rounded_rect(d, NB_X + NB_W // 2, NB_Y, NB_X + NB_W, NB_Y + NB_H, RAD, PAPER_ALT)
# spine fold
d.line((NB_X + NB_W // 2, NB_Y + px(5),
        NB_X + NB_W // 2, NB_Y + NB_H - px(5)),
       fill=ACCENT_DARK, width=px(2))

# ruled lines (left page)
for i in range(5):
    y = NB_Y + px(15) + i * px(9.5)
    d.line((NB_X + px(8), y, NB_X + NB_W // 2 - px(4), y),
           fill=RULE, width=px(3))

# a short "text" accent line on the left page (what you actually write)
d.line((NB_X + px(8), NB_Y + px(15), NB_X + px(30), NB_Y + px(15)),
       fill=ACCENT, width=px(4))

# ---- audio note-wave glyph (right page) ------------------------------------
cx = NB_X + NB_W * 3 // 4
cy = NB_Y + NB_H // 2
d.arc((cx - px(9), cy - px(9), cx + px(9), cy + px(9)),
      start=310, end=50, fill=ACCENT, width=px(4))
d.arc((cx - px(3.5), cy - px(15), cx + px(3.5), cy + px(15)),
      start=310, end=50, fill=ACCENT, width=px(4))

# ---- pen (diagonal across the bottom-right of the tile) ---------------------
p0 = (px(63), px(70))
p1 = (px(89), px(90))
pw = px(6)

# body
d.line((p0[0], p0[1], p1[0], p1[1]), fill=PENAME, width=pw)
# tip (darker, pointing down-right, past the notebook edge)
tip0 = (p1[0] + px(3), p1[1] + px(2))
tip1 = (p1[0] + px(12), p1[1] + px(10))
d.line((p1[0], p1[1], tip1[0], tip1[1]), fill=ACCENT_DARK, width=pw)
# highlight on body
d.line((p0[0] - px(3), p0[1] - px(3), p1[0] - px(3), p1[1] - px(3)),
       fill=(255, 255, 255, 130), width=px(2))
# clip cap
d.ellipse((p0[0] - pw, p0[1] - pw, p0[0] + pw, p0[1] + pw), fill=(255, 255, 255, 90))
d.ellipse((p0[0] - px(1.5), p0[1] - px(1.5), p0[0] + px(1.5), p0[1] + px(1.5)),
          fill=ACCENT_DARK)

# ---- highlight star (top-right) ----------------------------------------------
sx, sy = px(83), px(17)
sr = px(8)
star = []
for k in range(10):
    rad = sr if k % 2 == 0 else sr / 2.4
    ang = -math.pi / 2 + k * math.pi / 5
    star.append((sx + rad * math.cos(ang), sy + rad * math.sin(ang)))
d.polygon(star, fill=HIGHLIGHT)

# ---- produce output ----------------------------------------------------------
# downscale to working sizes
img256 = img.resize((256, 256), Image.LANCZOS)
img512 = img.resize((512, 512), Image.LANCZOS)
img256.save(PNG_256, "PNG")
img512.save(PNG_512, "PNG")

# multi-res .ico
ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128),
             (256, 256)]
frames = [img.resize(s, Image.LANCZOS) for s in ico_sizes]
frames[-1].save(ICO_PATH, format="ICO", sizes=ico_sizes,
                append_images=frames[:-1])

print("Saved:", os.path.abspath(ICO_PATH))
print("Saved:", os.path.abspath(PNG_256))
print("Saved:", os.path.abspath(PNG_512))
