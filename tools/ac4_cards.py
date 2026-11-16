"""Spanish mission title cards: the card shown before each briefing
("'THE NORTHERN EYE'", "1628hrs. 10 October 2004", "MISSION 03").

Each card is entry 187 + (mission - 1) of DATA.CDP: a 544x192 8-bit image
(GIF IMAGE packet at 0x80) followed by its own 256-colour CT24 palette.  The
cards are decoded to RGB, the three text lines are rebuilt from letters
taken from all 18 cards (each letter with its soft halo, at the card's own
spacing), and the result is quantised back to 256 colours: new indices and
a new palette, written over the old ones in place (same size).

    title  'EL OJO DEL NORTE'          (ejecutable.json's mission names)
    date   1628hrs. 10 Octubre 2004    (only the month changes)
    footer MISION 03

Letters no card has are made from others: J (U without the top of its left
side), Q (O with a tail), Z and z (S and s mirrored), E (F with a bottom bar).

    python ac4_cards.py <us.iso> <preview.png>
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ac4_cdp import Cdp

W, H = 544, 192
IMG = 0x80
CLUT = IMG + W * H + 96
FIRST = 187
TITLES = ["SITTING DUCK", "IMMINENT THREAT", "THE NORTHERN EYE", "BLOCKADE", "LIFELINE",
          "INVINCIBLE FLEET", "DEEP STRIKE", "SHATTERED SKIES", "OPERATION BUNKER SHOT",
          "TANGO LINE", "ESCORT", "STONEHENGE OFFENSIVE", "SAFE RETURN", "BREAKING ARROWS",
          "EMANCIPATION", "WHISKEY CORRIDOR", "SIEGE OF FARBANTI", "MEGALITH"]
TITLES_ES = ["BLANCO FACIL", "AMENAZA CERCANA", "EL OJO DEL NORTE", "BLOQUEO", "SALVAVIDAS",
             "FLOTA INVENCIBLE", "ATAQUE PROFUNDO", "CIELOS ROTOS", "OPERACION BUNKER SHOT",
             "LINEA TANGO", "ESCOLTA", "OFENSIVA STONEHENGE", "REGRESO SEGURO",
             "FLECHAS ROTAS", "EMANCIPACION", "CORREDOR WHISKEY", "ASEDIO DE FARBANTI",
             "MEGALITO"]
KEEP_ENGLISH_TITLES = True   # the user's choice (2026-11-16): mission names in English
MONTH_OF = ["September", "October", "October", "November", "November", "November",
            "December", "December", "January", "February", "March", "April", "May",
            "June", "July", "August", "September", "September"]
MONTHS = {"January": "Enero", "February": "Febrero", "March": "Marzo", "April": "Abril",
          "May": "Mayo", "June": "Junio", "July": "Julio", "August": "Agosto",
          "September": "Septiembre", "October": "Octubre", "November": "Noviembre",
          "December": "Diciembre"}
CORE = 170          # a pixel belongs to a letter (not its halo) above this;
                    # lower, the halos join neighbouring letters of the date
FLOOR = 70          # only a letter's own pixels (above this) are copied; the
                    # soft glow around them is made again for the whole line
                    # (render_band), so no letter brings a box of old glow


def sw(i):
    return (i & ~0x18) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)


def decode(blob):
    clut = blob[CLUT:CLUT + 768]
    pal = [tuple(clut[sw(i) * 3:sw(i) * 3 + 3]) for i in range(256)]
    idx = blob[IMG:IMG + W * H]
    return [[pal[idx[y * W + x]] for x in range(W)] for y in range(H)]


def lum(p):
    return max(p)


def bands(img):
    """(y0, y1) of the title, date and footer lines, with room for the halo"""
    ink = [max(lum(p) for p in row) > CORE for row in img]
    out, y = [], 0
    while y < H:
        if ink[y]:
            s = y
            while y < H and ink[y]:
                y += 1
            out.append((s, y))
        y += 1
    rule = [y for y in range(H) if sum(1 for p in img[y] if lum(p) > CORE) > W // 2]
    out = [b for b in out if b[1] - b[0] > 3]          # not the rule
    if len(out) != 3:
        raise SystemExit("cards: expected 3 text lines, found %r" % out)
    res = []
    for a, b in out:
        lo, hi = max(0, a - 10), min(H, b + 10)        # the glow reaches ~10 rows
        for r in rule:                                 # but never over the rule
            if r < a:
                lo = max(lo, r + 3)
            elif r >= b:
                hi = min(hi, r - 2)
        res.append((lo, hi))
    return res


def runs(img, y0, y1):
    cols = [max(lum(img[y][x]) for y in range(y0, y1)) > CORE for x in range(W)]
    out, x = [], 0
    while x < W:
        if cols[x]:
            s = x
            while x < W and cols[x]:
                x += 1
            out.append((s, x))
        x += 1
    return out


def split_words(rs, gap):
    out = [[0]]
    for k in range(1, len(rs)):
        if rs[k][0] - rs[k - 1][1] > gap:
            out.append([])
        out[-1].append(k)
    return out


class Glyph:
    def __init__(self, pix, c0, c1):
        self.pix, self.c0, self.c1 = pix, c0, c1

    @property
    def w(self):
        return len(self.pix[0])


def glyph_at(img, y0, y1, rs, k, halo):
    """letter k with its halo (up to the middle of the gaps around it)"""
    a, b = rs[k]
    lo = max(a - halo, (rs[k - 1][1] + a) // 2 if k else 0)
    hi = min(b + halo, (b + rs[k + 1][0] + 1) // 2 if k + 1 < len(rs) else W)
    return Glyph([row[lo:hi] for row in img[y0:y1]], a - lo, b - lo)


def mirror(g):
    return Glyph([row[::-1] for row in g.pix], g.w - g.c1, g.w - g.c0)


def ink_rows(g):
    return [y for y in range(len(g.pix)) if any(lum(p) > CORE for p in g.pix[y])]


def make_missing(gl, title):
    bg = (0, 0, 0)
    if "J" not in gl and "U" in gl:
        u = gl["U"]
        pix = [list(r) for r in u.pix]
        ys = ink_rows(u)
        stop = ys[0] + (ys[-1] - ys[0]) * 3 // 5
        keep_from = u.c0 + max(2, (u.c1 - u.c0) // 3)
        for y in range(0, stop):
            for x in range(0, keep_from):
                pix[y][x] = bg
        gl["J"] = Glyph(pix, u.c0, u.c1)
    if "Q" not in gl and "O" in gl:
        o = gl["O"]
        pix = [list(r) for r in o.pix]
        bright = max((p for r in pix for p in r), key=lum)
        yb = ink_rows(o)[-1]
        for k in range(3):
            y, x = yb - 1 + k, o.c1 - 3 + k
            if 0 <= y < len(pix) and 0 <= x < o.w:
                pix[y][x] = bright
        gl["Q"] = Glyph(pix, o.c0, o.c1)
    for big, small in (("Z", "S"), ("z", "s")):
        if big not in gl and small in gl:
            gl[big] = mirror(gl[small])
    if "E" not in gl and "F" in gl:
        f = gl["F"]
        pix = [list(r) for r in f.pix]
        ys = ink_rows(f)
        top, bot = ys[0], ys[-1]
        for x in range(f.w):
            for dy in (0, 1):
                if lum(pix[top + dy][x]) > lum(pix[bot - dy][x]):
                    pix[bot - dy][x] = pix[top + dy][x]
        gl["E"] = Glyph(pix, f.c0, f.c1)


def clear(img, src, y0, y1, x0, x1):
    """wipe the old text: each row of [x0, x1) gets the faintest colour that
    row had there (the soft glow the line sits on, not black)"""
    x0, x1 = max(0, x0), min(W, x1)
    for y in range(y0, y1):
        for x in range(x0, x1):
            img[y][x] = (0, 0, 0)
    # the halo of the old letters fades out over a few pixels each side
    for y in range(y0, y1):
        for x in list(range(max(0, x0 - 6), x0)) + list(range(x1, min(W, x1 + 6))):
            img[y][x] = (0, 0, 0)


def place(img, y0, y1, items, centre, extent, src):
    """items: (Glyph, cell width) or (None, gap); the old line (extent) is
    wiped and the letters set centred in their cells, the whole centred on
    `centre`"""
    width = sum(w for _, w in items)
    x = centre - width // 2
    clear(img, src, y0, y1, *extent)
    for g, w in items:
        if g is not None:
            gx = x + (w - (g.c1 - g.c0)) // 2 - g.c0
            for dy, row in enumerate(g.pix):
                if y0 + dy >= y1:
                    break
                for dx, p in enumerate(row):
                    xx = gx + dx
                    if 0 <= xx < W and lum(p) > max(FLOOR, lum(img[y0 + dy][xx])):
                        img[y0 + dy][xx] = p
        x += w
    return width


def analyse(cdp):
    cards, tg, dg = [], {}, {}
    for m in range(18):
        blob = cdp.get(FIRST + m)
        img = decode(blob)
        (t0, t1), (d0, d1), (f0, f1) = bands(img)
        trs = runs(img, t0, t1)
        letters = "'" + TITLES[m].replace(" ", "") + "'"
        if len(trs) != len(letters):
            raise SystemExit("cards: mission %d: %d title letters, %d expected"
                             % (m + 1, len(trs), len(letters)))
        cen = [(a + b) // 2 for a, b in trs]
        steps = sorted(b - a for a, b in zip(cen, cen[1:]))
        pitch = steps[len(steps) // 3]
        for k, ch in enumerate(letters):
            tg.setdefault(ch, glyph_at(img, t0, t1, trs, k, pitch // 2))
        drs = runs(img, d0, d1)
        gaps = sorted(b[0] - a[1] for a, b in zip(drs, drs[1:]))
        words = split_words(drs, gaps[len(gaps) // 2] * 3 // 2)   # 4 within, 8 between
        month = MONTH_OF[m]
        mw = words[-2] if len(words) >= 4 else None     # the word before the year
        if mw is None or len(mw) != len(month):
            raise SystemExit("cards: mission %d: cannot find %s on the card" % (m + 1, month))
        for k, ch in zip(mw, month):
            dg.setdefault(ch, glyph_at(img, d0, d1, drs, k, 4))
        for k in words[0]:                         # "hrs." gives h, r, s
            pass
        hw = words[0]
        for k, ch in zip(hw[-4:], "hrs."):
            dg.setdefault(ch, glyph_at(img, d0, d1, drs, k, 4))
        frs = runs(img, f0, f1)
        cards.append({"blob": blob, "img": img, "bands": ((t0, t1), (d0, d1), (f0, f1)),
                      "trs": trs, "pitch": pitch, "drs": drs, "words": words, "mw": mw,
                      "frs": frs})
    make_missing(tg, True)
    make_missing(dg, False)
    return cards, tg, dg


GLOW_SRC = 150      # the outer glow is made from the bright middle of the
                    # strokes only; with the dimmer edges it turns them fat


def _band_image(rows, cores_only, floor=None):
    from PIL import Image
    floor = FLOOR if floor is None else floor
    h, w = len(rows), len(rows[0])
    im = Image.new("RGB", (w, h))
    im.putdata([p if (not cores_only or lum(p) > floor) else (0, 0, 0)
                for row in rows for p in row])
    return im


def _glow(core_im, radius, gain, src_im=None):
    """the letters (core_im) over the glow of their bright middle"""
    from PIL import ImageChops, ImageFilter
    if src_im is None:
        src_im = core_im
    blur = src_im.filter(ImageFilter.GaussianBlur(radius))
    blur = blur.point(lambda v: min(255, int(v * gain)))
    return ImageChops.lighter(core_im, ImageChops.add(src_im, blur))


_FIT = {}


def fit_glow(img, y0, y1, key):
    """the blur radius and gain that best rebuild this line's glow from its
    letters"""
    if key in _FIT:
        return _FIT[key]
    from PIL import ImageChops, ImageStat
    rows = [r for r in img[y0:y1]]
    orig = _band_image(rows, False)
    core = _band_image(rows, True)
    bright = _band_image(rows, True, GLOW_SRC)
    best = None
    for radius in (1.5, 2, 2.5, 3, 4, 5, 6):
        for gain in (0.6, 0.8, 1.0, 1.3, 1.6, 2.0, 2.5, 3.0, 4.0):
            d = ImageChops.difference(_glow(core, radius, gain, bright), orig)
            err = sum(ImageStat.Stat(d).mean)
            if best is None or err < best[0]:
                best = (err, radius, gain)
    _FIT[key] = best[1:]
    return _FIT[key]


def render_band(img, y0, y1, params):
    """keep only the letters of the band, then give them their glow again"""
    radius, gain = params
    rows = img[y0:y1]
    out = _glow(_band_image(rows, True), radius, gain, _band_image(rows, True, GLOW_SRC))
    px = list(out.get_flattened_data() if hasattr(out, "get_flattened_data") else out.getdata())
    w = W
    for y in range(y0, y1):
        img[y] = list(img[y])
        for x in range(w):
            img[y][x] = px[(y - y0) * w + x]


def rebuild(c, m, tg, dg):
    img = [list(r) for r in c["img"]]
    (t0, t1), (d0, d1), (f0, f1) = c["bands"]
    centre = W // 2
    # title: letters on the card's pitch, words a pitch apart as on the card
    trs, pitch = c["trs"], c["pitch"]
    items = []
    # mission names stay in English (as in the game's menus): the card keeps
    # its own title line, only the date and the footer are rebuilt
    words = [] if KEEP_ENGLISH_TITLES else TITLES_ES[m].split(" ")
    for wi, word in enumerate(words):
        text = ("'" if wi == 0 else "") + word + ("'" if wi == len(words) - 1 else "")
        for ch in text:
            items.append((tg[ch], pitch))
        if wi + 1 < len(words):
            items.append((None, pitch))
    if sum(w for _, w in items) > W - 8:
        raise SystemExit("cards: mission %d title does not fit" % (m + 1))
    if items:
        place(img, t0, t1, items, centre,
              (trs[0][0] - pitch // 2, trs[-1][1] + pitch // 2), c["img"])
    # date: everything but the month as it was, the month in Spanish
    drs, words, mw = c["drs"], c["words"], c["mw"]
    src = c["img"]
    before = (max(0, drs[0][0] - 5), drs[mw[0]][0])          # up to the month
    after = (drs[mw[-1]][1], min(W, drs[-1][1] + 5))         # from its end
    month_en = MONTH_OF[m]
    month_es = MONTHS[month_en]
    inner = [drs[k + 1][0] - drs[k][1] for k in mw[:-1]]
    gap = sorted(inner)[len(inner) // 2]
    left_gap = drs[mw[0]][0] - drs[mw[0] - 1][1]
    right_gap = drs[mw[-1] + 1][0] - drs[mw[-1]][1]
    pieces = [("cols", before[0], before[1] - left_gap)]
    pieces.append(("gap", left_gap))
    for i, ch in enumerate(month_es):
        g = dg[ch]
        pieces.append(("glyph", g))
        if i + 1 < len(month_es):
            pieces.append(("gap", gap))
    pieces.append(("gap", right_gap))
    pieces.append(("cols", after[0] + right_gap, after[1]))
    width = sum((p[2] - p[1]) if p[0] == "cols" else (p[1] if p[0] == "gap" else p[1].c1 - p[1].c0)
                for p in pieces)
    clear(img, src, d0, d1, drs[0][0] - 8, drs[-1][1] + 8)
    x = centre - width // 2
    for p in pieces:
        if p[0] == "cols":
            for sx in range(p[1], p[2]):
                for y in range(d0, d1):
                    if 0 <= x < W and lum(src[y][sx]) > max(FLOOR, lum(img[y][x])):
                        img[y][x] = src[y][sx]
                x += 1
        elif p[0] == "gap":
            x += p[1]
        else:
            g = p[1]
            for dy, row in enumerate(g.pix):
                for dx, px in enumerate(row):
                    xx = x - g.c0 + dx
                    if 0 <= xx < W and d0 + dy < d1 and lum(px) > max(FLOOR, lum(img[d0 + dy][xx])):
                        img[d0 + dy][xx] = px
            x += g.c1 - g.c0
    # footer: MISSION nn -> MISION nn (drop the second S)
    frs = c["frs"]
    if len(frs) >= 9:
        keep = [k for k in range(len(frs)) if k != 3]
        lo = max(0, frs[0][0] - 5)
        hi = min(W, frs[-1][1] + 5)
        cols = []
        for k in keep:
            a = (frs[k - 1][1] + frs[k][0]) // 2 if k else lo
            b = (frs[k][1] + frs[k + 1][0]) // 2 if k + 1 < len(frs) else hi
            cols.append((a, b))
        # drop S2 together with the gap after S1
        width = sum(b - a for a, b in cols)
        x = centre - width // 2
        line = [list(r) for r in src[f0:f1]]
        clear(img, src, f0, f1, lo, hi)
        for a, b in cols:
            for sx in range(a, b):
                for y in range(f0, f1):
                    if 0 <= x < W and lum(line[y - f0][sx]) > max(FLOOR, lum(img[y][x])):
                        img[y][x] = line[y - f0][sx]
                x += 1
    # the glow, made again around the new letters as it was around the old
    src = c["img"]
    for (y0, y1), kind in zip(c["bands"], ("title", "date", "footer")):
        render_band(img, y0, y1, fit_glow(src, y0, y1, "%s%d" % (kind, m)))
    return img


def encode(c, img):
    from PIL import Image
    im = Image.new("RGB", (W, H))
    im.putdata([p for row in img for p in row])
    q = im.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    pal = q.getpalette()[:768]
    pal += [0] * (768 - len(pal))
    clut = bytearray(768)
    for i in range(256):
        j = sw(i)
        clut[j * 3:j * 3 + 3] = bytes(pal[i * 3:i * 3 + 3])
    return bytes(q.tobytes()), bytes(clut)


def card_writes(cdp):
    """entry -> [(offset, bytes)]"""
    cards, tg, dg = analyse(cdp)
    out = {}
    for m, c in enumerate(cards):
        img = rebuild(c, m, tg, dg)
        idx, clut = encode(c, img)
        out[FIRST + m] = [(IMG, idx), (CLUT, clut)]
    return out


if __name__ == "__main__":
    from PIL import Image
    cdp = Cdp(sys.argv[1])
    cards, tg, dg = analyse(cdp)
    sheet = Image.new("RGB", (W * 2, H * 18), (40, 0, 40))
    for m, c in enumerate(cards):
        img = rebuild(c, m, tg, dg)
        idx, clut = encode(c, img)
        pal = [clut[sw(i) * 3 + k] for i in range(256) for k in range(3)]
        q = Image.frombytes("P", (W, H), idx)
        q.putpalette(pal)
        old = Image.new("RGB", (W, H))
        old.putdata([p for row in c["img"] for p in row])
        sheet.paste(old, (0, m * H))
        sheet.paste(q.convert("RGB"), (W, m * H))
    sheet.save(sys.argv[2])
    print("ok")
