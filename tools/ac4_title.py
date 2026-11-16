"""Spanish for the title screen's "- PRESS START BUTTON -": "- PRESIONA START -",
like the menu text of the same name (ejecutable.json).

The line is two 424x48 8-bit images in the title pack (entry 176), kept as GIF
IMAGE packets: the letters and their glow, drawn on top of each other.  The
new line is made of the letters of the old one (I is T's stem), each with its
own patch of glow, at the old spacing; the result is written over the old
images in place (same size, nothing moves).

    python ac4_title.py <us.iso> <preview.png>
"""
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ac4_cdp import Cdp

ENTRY = 176
W, H = 424, 48
OLD = "-PRESSSTARTBUTTON-"
WORDS_OLD = ["-", "PRESS", "START", "BUTTON", "-"]
NEW = ["-", "PRESIONA", "START", "-"]
INK = 100                      # a column belongs to a letter above this
                               # (each letter also has a faint halo)


def images(blob):
    """(offset of letters, offset of glow): the two 424x48 IMAGE packets
    (tag NLOOP 0x4F8 = 20352 bytes) whose letter runs spell OLD"""
    tag = struct.pack("<I", 0x4F8) + bytes(3) + b"\x08"
    found, at = [], 0
    while True:
        at = blob.find(tag, at)
        if at < 0:
            break
        found.append(at + 16)
        at += 16
    for i, a in enumerate(found):
        for b in found[i + 1:]:
            t, g = blob[a:a + W * H], blob[b:b + W * H]
            if len(runs(t)) == len(OLD):
                return a, b
            if len(runs(g)) == len(OLD):
                return b, a
    raise SystemExit("title: the PRESS START BUTTON images are not in entry %d" % ENTRY)


def col_ink(img, x):
    return max(img[y * W + x] for y in range(H))


def runs(img):
    cols = [col_ink(img, x) > INK for x in range(W)]
    out, x = [], 0
    while x < W:
        if cols[x]:
            s = x
            while x < W and cols[x]:
                x += 1
            out.append((s, x))
        x += 1
    return out


def build(blob):
    """Each word sits in a rounded glow capsule.  The line is rebuilt from
    column ranges of the old one, the same for both images: the dashes and
    START as they were, PRESIONA as PRESS's left cap, one 20-pixel cell per
    letter (cells taken from wherever that letter is; I is T's stem in its
    own cell) and PRESS's right cap."""
    ta, ga = images(blob)
    t, g = blob[ta:ta + W * H], blob[ga:ga + W * H]
    rs = runs(t)
    # word regions: from the middle of the gap before to the middle after
    words, k = [], 0
    for word in WORDS_OLD:
        words.append(list(range(k, k + len(word))))
        k += len(word)
    bounds = [0]
    for w0, w1 in zip(words, words[1:]):
        bounds.append((rs[w0[-1]][1] + rs[w1[0]][0]) // 2)
    bounds.append(W)
    region = {WORDS_OLD[i] + str(i): (bounds[i], bounds[i + 1]) for i in range(len(WORDS_OLD))}
    starts = [rs[i][0] for i in words[1]]
    pitch = sorted(b - a for a, b in zip(starts, starts[1:]))[len(starts) // 2 - 1]

    def cell(i):
        c = (rs[i][0] + rs[i][1]) // 2
        return (c - pitch // 2, c - pitch // 2 + pitch)

    cells = {}
    for i, c in enumerate(OLD):
        cells.setdefault(c, cell(i))
    # I: T's stem, centred in a cell of its own
    a, b = rs[OLD.index("T")]
    stem = [x for x in range(a, b) if max(t[y * W + x] for y in range(H // 2, H)) > INK]
    sc = (stem[0] + stem[-1] + 1) // 2
    cells["I"] = (sc - pitch // 2, sc - pitch // 2 + pitch)
    keep_i = range(stem[0] - 2, stem[-1] + 3)        # the stem and its soft edge
    press = region["PRESS1"]
    first, last = cell(words[1][0]), cell(words[1][-1])
    # (text columns, glow columns, text columns to keep or None for all)
    pieces = [(region["-0"],) * 2 + (None,), ((press[0], first[0]),) * 2 + (None,)]
    for c in NEW[1]:
        if c == "I":            # T's stem, under an S's glow (no T-shaped glow)
            pieces.append((cells["I"], cells["S"], keep_i))
        else:
            pieces.append((cells[c], cells[c], None))
    pieces += [((last[1], press[1]),) * 2 + (None,), (region["START2"],) * 2 + (None,),
               (region["-4"],) * 2 + (None,)]
    width = sum(b - a for (a, b), _, _ in pieces)
    if width > W:
        raise SystemExit("title: %s does not fit" % " ".join(NEW))
    x = (W - width) // 2
    nt, ng = bytearray(W * H), bytearray(W * H)
    # the I cell's letters image: S's cell with the S wiped back to the halo
    # around it, and T's stem (without the bar) drawn in the middle
    s0 = cells["S"][0]
    sa, sb = rs[OLD.index("S")]
    icell = [[t[y * W + s0 + k] for k in range(pitch)] for y in range(H)]
    for y in range(H):
        edge = min(icell[y][0], icell[y][pitch - 1])
        for k in range(sa - s0 - 1, sb - s0 + 1):
            if 0 <= k < pitch:
                icell[y][k] = edge
    bar = [y for y in range(H)
           if max(t[y * W + xx] for xx in range(a, b) if xx not in keep_i) > INK]
    below = max(bar) + 1 if bar else 0
    i0 = cells["I"][0]
    for k in keep_i:
        for y in range(H):
            src = below if y in bar else y
            v = t[src * W + k]
            if y in bar and min(bar) > 0 and t[(min(bar) - 1) * W + k] < INK:
                v = t[src * W + k]
            icell[y][k - i0] = max(icell[y][k - i0], v)
    for (a, b), (ga_, _), keep in pieces:
        for k in range(b - a):
            sx, sg = a + k, ga_ + k
            for y in range(H):
                j = y * W + x
                if keep is not None:
                    nt[j] = icell[y][k]
                elif 0 <= sx < W:
                    nt[j] = t[y * W + sx]
                if 0 <= sg < W:
                    ng[j] = g[y * W + sg]
            x += 1
    return [(ta, bytes(nt)), (ga, bytes(ng))]


def title_writes(cdp):
    """entry -> [(offset, bytes)]"""
    return {ENTRY: build(cdp.get(ENTRY))}


if __name__ == "__main__":
    from PIL import Image
    cdp = Cdp(sys.argv[1])
    blob = cdp.get(ENTRY)
    ta, ga = images(blob)
    w = build(blob)
    ims = [blob[ta:ta + W * H], blob[ga:ga + W * H], w[0][1], w[1][1]]
    sheet = Image.new("L", (W, (H + 4) * 5), 40)
    for k, b in enumerate(ims):
        sheet.paste(Image.frombytes("L", (W, H), bytes(b)).point(lambda v: min(255, v * 2)),
                    (0, k * (H + 4)))
    comb = Image.frombytes("L", (W, H), bytes(max(a, b) for a, b in zip(w[0][1], w[1][1])))
    sheet.paste(comb.point(lambda v: min(255, v * 2)), (0, 4 * (H + 4)))
    sheet.save(sys.argv[2])
    print("ok")
