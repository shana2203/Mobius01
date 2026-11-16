"""Spanish for the English text images of the replay / demo screens (part [1]
of every mission pack, the same in all 100): "CAMERA ANGLE", "PRESS START
BUTTON TO EXIT REPLAY", "PRESS START BUTTON TO SKIP" and the boxed
"DEMONSTRATION".  The new images are composed from the glyphs of the images
themselves (US disc only); "REPLAY" stays, "REPETICION" does not fit its box.

    python ac4_strips.py <us.iso> <preview.png>
"""
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ac4_cdp import Cdp
import ac4_es as E

DATA = 0x20


def size(g):
    return struct.unpack_from("<HH", g, 0x1C)


def get4(g):
    w, h = size(g)
    px = []
    for c in g[DATA:DATA + w * h // 2]:
        px += [c & 15, c >> 4]
    return w, h, [px[y * w:(y + 1) * w] for y in range(h)]


def put4(g, img):
    w, h = size(g)
    out = bytearray(g)
    for y in range(h):
        for x in range(0, w, 2):
            out[DATA + (y * w + x) // 2] = img[y][x] | (img[y][x + 1] << 4)
    return bytes(out)


def get8(g):
    w, h = size(g)
    p = g[DATA:DATA + w * h]
    return w, h, [list(p[y * w:(y + 1) * w]) for y in range(h)]


def put8(g, img):
    w, h = size(g)
    out = bytearray(g)
    for y in range(h):
        out[DATA + y * w:DATA + (y + 1) * w] = bytes(img[y])
    return bytes(out)


def runs(img, y0, y1, x0, x1):
    """column runs with ink between rows y0..y1"""
    ink = [any(img[y][x] for y in range(y0, y1)) for x in range(x0, x1)]
    out, x = [], 0
    while x < len(ink):
        if ink[x]:
            s = x
            while x < len(ink) and ink[x]:
                x += 1
            out.append((x0 + s, x0 + x))
        x += 1
    return out


def glyph_map(img, text, y0, y1, x0, x1):
    """letter -> columns, reading `text` (spaces skipped) from the image"""
    letters = [c for c in text if c != " "]
    rs = runs(img, y0, y1, x0, x1)
    if len(rs) < len(letters):
        raise ValueError("%r: %d glyphs for %d letters" % (text, len(rs), len(letters)))
    out = {}
    for c, (a, b) in zip(letters, rs):
        out.setdefault(c, [row[a:b] for row in img[y0:y1]])
    return out


def compose(img, glyphs, text, y0, x, gap, space, clear):
    cx0, cx1 = clear
    for y in range(y0, y0 + len(next(iter(glyphs.values())))):
        for xx in range(cx0, cx1):
            img[y][xx] = 0
    for c in text:
        if c == " ":
            x += space
            continue
        g = glyphs[c]
        for dy, row in enumerate(g):
            for dx, v in enumerate(row):
                if v:
                    img[y0 + dy][x + dx] = v
        x += len(g[0]) + gap
    return x


def build(us):
    b = us.get(1)
    small = {}
    parts = {}
    # the 4-bit strips, 10-pixel font (8 wide, pitch 10, a space adds 4)
    cam = E.part_at(b, [1, 0, 0])
    exit_ = E.part_at(b, [1, 2, 0])
    skip = E.part_at(b, [1, 2, 1])
    for g, t in ((cam, "CAMERA ANGLE"), (exit_, "PRESS START BUTTON TO EXIT REPLAY"),
                 (skip, "PRESS START BUTTON TO SKIP")):
        w, h, img = get4(g)
        ends = runs(img, 0, 10, 0, w)
        n = len([c for c in t if c != " "])
        small.update(glyph_map(img, t, 0, 10, 0, ends[n - 1][1]))
    # CAMERA ANGLE <> [] O: the word, then the button icons moved up to it
    w, h, img = get4(cam)
    rs = runs(img, 0, 10, 0, w)
    icons = (rs[11][0], w)                          # from the "<" after ANGLE
    icon_cols = [row[icons[0]:icons[1]] for row in img]
    x = compose(img, small, "CAMARA", 0, 0, 2, 4, (0, w))
    x += 4
    for y, row in enumerate(icon_cols):
        for dx, v in enumerate(row):
            if x + dx < w:
                img[y][x + dx] = v
    parts[(1, 0, 0)] = put4(cam, img)
    for g, path, t in ((exit_, (1, 2, 0), "PRESIONA START PARA SALIR"),
                       (skip, (1, 2, 1), "PRESIONA START PARA SALTAR")):
        w, h, img = get4(g)
        end = compose(img, small, t, 0, 0, 2, 4, (0, w))
        if end > w:
            raise ValueError("%r does not fit %d" % (t, w))
        parts[path] = put4(g, img)
    # DEMONSTRATION, 8-bit, boxed: letters between rows 7..21, inside 1..186
    demo = E.part_at(b, [1, 1, 1])
    w, h, img = get8(demo)
    bg = img[10][100]                       # the box's dark palette index
    work = [[v if v > 0x20 else 0 for v in row] for row in img]
    big = glyph_map(work, "DEMONSTRATION", 7, 21, 2, 186)
    o = [list(r) for r in big["O"]]                  # C: the O without its right side
    cw = len(o[0])
    for y in range(3, len(o) - 3):
        for x in range(cw - 3, cw):
            o[y][x] = 0
    big["C"] = o
    t = "DEMOSTRACION"
    width = sum(len(big[c][0]) for c in t) + 3 * (len(t) - 1)
    x0 = 2 + max(0, (186 - 2 - width) // 2)
    compose(work, big, t, 7, x0, 3, 0, (2, 186))
    for y in range(7, 21):
        for x in range(2, 186):
            img[y][x] = work[y][x] if work[y][x] else bg
    parts[(1, 1, 1)] = put8(demo, img)
    return parts


def strip_parts(us):
    """entry -> [(path, data)] for the mission packs whose part [1] images are
    the US ones"""
    new = build(us)
    ref = us.get(1)
    out = {}
    for e in range(1, 101):
        try:
            blob = us.get(e)
        except Exception:
            continue
        lst = []
        for path, data in new.items():
            try:
                if E.part_at(blob, list(path)) == E.part_at(ref, list(path)):
                    lst.append((list(path), data))
            except Exception:
                pass
        if lst:
            out[e] = lst
    return out


if __name__ == "__main__":
    from PIL import Image
    us = Cdp(sys.argv[1])
    parts = build(us)
    ims = []
    for path in sorted(parts):
        g = parts[path]
        w, h = size(g)
        if g[0x13] == 0x14:
            _, _, img = get4(g); px = [v * 17 for r in img for v in r]
        else:
            _, _, img = get8(g); px = [v for r in img for v in r]
        im = Image.new("L", (w, h)); im.putdata(px); ims.append(im)
    W = max(i.width for i in ims) * 3
    H = sum(i.height * 3 + 6 for i in ims)
    m = Image.new("L", (W, H), 40); y = 0
    for i in ims:
        m.paste(i.resize((i.width * 3, i.height * 3), Image.NEAREST), (0, y)); y += i.height * 3 + 6
    m.save(sys.argv[2])
    print("ok", sorted(parts))
