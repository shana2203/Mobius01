"""Spanish HUD labels: rewrites the boxed labels of the HUD atlas (a 4-bit
256x256 image at the start of part [0] of every mission pack, entries 1-100)
with glyphs taken from the atlas's own block font, so every HUD colour (each
a different palette) changes at once.

    python ac4_hud.py <us.iso> <out_part0.bin> <preview.png>
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from PIL import Image
from ac4_cdp import Cdp
import ac4_es as E

DATA_OFF, W, H = 112, 256, 256

# label boxes: x0, x1 (inner, exclusive of the frame), text; rows 100..107
BOX_Y0, BOX_Y1, TEXT_Y = 97, 111, 100
LABELS = {           # box (left to right) -> Spanish; None keeps the original
    0: None, 1: "OBJ", 2: "TPO", 3: "VEL", 4: None, 5: "DAÑO",
}
# the boxes' own 8-pixel letters: (box, x of the letter, letter)
BOX_LETTERS = [(0, 4, "P"), (0, 13, "T"), (0, 22, "S"), (1, 49, "G"), (2, 88, "M"),
               (2, 97, "E"), (3, 152, "D"), (4, 170, "A"), (4, 179, "L")]
FONT_ROWS = [(28, "0123456789ABCDEFGHIJK"), (36, "LMNOPQRSTUVWXYZ")]
GLYPH_H = 8


def load(iso):
    part = E.part_at(Cdp(iso).get(1), [0])
    px = []
    for b in part[DATA_OFF:DATA_OFF + W * H // 2]:
        px += [b & 15, b >> 4]
    return part, [px[y * W:(y + 1) * W] for y in range(H)]


def columns_with_ink(img, y0, h, x0=0, x1=W):
    return [any(img[y][x] for y in range(y0, y0 + h)) for x in range(x0, x1)]


def segment(img, y0, h, chars):
    """glyphs of a font row, split at empty columns"""
    ink = columns_with_ink(img, y0, h)
    runs, x = [], 0
    while x < W:
        if ink[x]:
            s = x
            while x < W and ink[x]:
                x += 1
            runs.append((s, x))
        x += 1
    glyphs = {}
    for c, (a, b) in zip(chars, runs):
        glyphs[c] = [row[a:b] for row in img[y0:y0 + h]]
    return glyphs, runs


def find_boxes(img):
    """the six label boxes: runs of frame columns on the box's top border row"""
    top = img[96]
    runs, x = [], 0
    while x < W:
        if top[x]:
            s = x
            while x < W and top[x]:
                x += 1
            runs.append((s, x))
        x += 1
    return [(a + 1, b - 1) for a, b in runs if b - a > 12][:6]


def draw(img, glyphs, text, x0, x1):
    gap = 1
    parts = []
    for c in text:
        if c == "Ñ":
            g = [list(r) for r in glyphs["N"]]
            parts.append(("N~", g))
        else:
            parts.append((c, glyphs[c]))
    width = sum(len(g[0]) for _, g in parts) + gap * (len(parts) - 1)
    for y in range(BOX_Y0, BOX_Y1):
        for x in range(x0, x1):
            img[y][x] = 0
    x = x0 + max(0, (x1 - x0 - width) // 2)
    for c, g in parts:
        gw = len(g[0])
        for dy in range(GLYPH_H):
            for dx in range(gw):
                if x + dx < x1 and g[dy][dx]:
                    img[TEXT_Y + dy][x + dx] = g[dy][dx]
        if c == "N~":                      # the tilde, two rows above the N
            mid = x + gw // 2
            for dx, dy in ((-2, 1), (-1, 0), (0, 1), (1, 1), (2, 0)):
                if x0 <= mid + dx < x1:
                    img[TEXT_Y - 2 + dy][mid + dx] = 15
        x += gw + gap
    return width <= x1 - x0


def narrow(g):
    """a 10-pixel letter of the atlas font as 8 pixels, like the boxes' own"""
    keep = [c for c in range(len(g[0])) if c not in (3, 6)] if len(g[0]) == 10 else range(len(g[0]))
    return [[row[c] for c in keep] for row in g]


# Other English words of the atlas.  The thin line fonts of TGT / FRND (7x7)
# and SHOOT (6x6) have no full alphabet in the atlas, so the few letters the
# Spanish needs are drawn here in the same one-pixel stroke; LEAD uses the
# atlas's own bold font.
THIN7 = {
    "O": ["#######", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#######"],
    "B": ["######.", "#.....#", "#.....#", "######.", "#.....#", "#.....#", "######."],
    "J": ["......#", "......#", "......#", "......#", "#.....#", "#.....#", "#######"],
    "A": ["#######", "#.....#", "#.....#", "#######", "#.....#", "#.....#", "#.....#"],
    "L": ["#......", "#......", "#......", "#......", "#......", "#......", "#######"],
    "I": ["#######", "...#...", "...#...", "...#...", "...#...", "...#...", "#######"],
}
THIN6 = {
    "F": ["######", "#.....", "#####.", "#.....", "#.....", "#....."],
    "U": ["#....#", "#....#", "#....#", "#....#", "#....#", "######"],
    "E": ["######", "#.....", "#####.", "#.....", "#.....", "######"],
    "G": ["######", "#.....", "#.....", "#..###", "#....#", "######"],
    "O": ["######", "#....#", "#....#", "#....#", "#....#", "######"],
}
# (text, font, clear box x0, y0, x1, y1 exclusive, first letter x, text y,
#  centre in the box)
WORDS = [
    ("OBJ", "thin7", 110, 144, 134, 151, 110, 144, False),     # TGT (target label)
    ("ALI", "thin7", 190, 134, 224, 142, 190, 134, False),     # FRND (friendly)
    ("FUEGO", "thin6", 99, 161, 137, 167, 101, 161, False),    # SHOOT
    ("GUIA", "bold", 106, 213, 147, 223, 0, 214, True),        # LEAD
]
# rows each word touches, for hud_parts (see ac4_es.py)
WORD_ROWS = [(134, 151), (161, 167), (213, 223)]


def pattern_glyph(rows):
    return [[15 if ch == "#" else 0 for ch in r] for r in rows]


def draw_word(img, glyphs, text, font, bx0, by0, bx1, by1, x, y, centre):
    if font == "bold":
        gs = [glyphs[c] for c in text]
    else:
        table = THIN7 if font == "thin7" else THIN6
        gs = [pattern_glyph(table[c]) for c in text]
    width = sum(len(g[0]) for g in gs) + (len(gs) - 1)
    for yy in range(by0, by1):
        for xx in range(bx0, bx1):
            img[yy][xx] = 0
    if centre:
        x = bx0 + max(0, (bx1 - bx0 - width) // 2)
    for g in gs:
        for dy, row in enumerate(g):
            for dx, v in enumerate(row):
                if v and x + dx < bx1:
                    img[y + dy][x + dx] = v
        x += len(g[0]) + 1
    return width <= bx1 - bx0


def main():
    part, img = load(sys.argv[1])
    glyphs = {}
    for y0, chars in FONT_ROWS:
        g, runs = segment(img, y0, GLYPH_H, chars)
        if len(runs) < len(chars):
            sys.exit("font row at y=%d: %d glyphs for %d characters" % (y0, len(runs), len(chars)))
        glyphs.update({c: narrow(v) for c, v in g.items()})
    for _, x, c in BOX_LETTERS:                  # the boxes' letters win
        glyphs[c] = [row[x:x + 8] for row in img[TEXT_Y:TEXT_Y + GLYPH_H]]
    boxes = find_boxes(img)
    print("boxes", boxes)
    for i, (x0, x1) in enumerate(boxes):
        if LABELS[i] is None:
            continue
        ok = draw(img, glyphs, LABELS[i], x0, x1)
        print("  %-6s in %3d..%3d %s" % (LABELS[i], x0, x1, "" if ok else "(too wide)"))
    for w in WORDS:
        ok = draw_word(img, glyphs, *w)
        print("  %-6s at %3d,%3d %s" % (w[0], w[2], w[3], "" if ok else "(too wide)"))
    data = bytearray()
    for y in range(H):
        for x in range(0, W, 2):
            data.append(img[y][x] | (img[y][x + 1] << 4))
    new = part[:DATA_OFF] + bytes(data) + part[DATA_OFF + len(data):]
    open(sys.argv[2], "wb").write(new)
    prev = Image.new("L", (W, H))
    prev.putdata([v * 17 for row in img for v in row])
    prev.crop((0, 90, 256, 230)).resize((1024, 560), Image.NEAREST).save(sys.argv[3])


if __name__ == "__main__":
    main()
