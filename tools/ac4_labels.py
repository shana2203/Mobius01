"""Spanish for the radio speaker labels (the small boxed word over the radio
subtitles): FRIEND -> ALIADO, ENEMY -> ENEMIGO, FLEET -> FLOTA; AWACS, RADAR,
the base name and the two flight call signs stay.

Each label is a 64x16 4-bit image sent as a GIF IMAGE packet (16-byte tag
"20 80 00 00 00 00 00 08", then 512 bytes).  The same four images are kept in
some 170 entries of DATA.CDP (the mission packs and the text / radio packs),
always as the same bytes, so they are found by content and rewritten in place
(same size; nothing moves -- see ac4_es.overlay).  The new words are drawn
with the labels' own letters (L and T from FLEET, G from RIGLEY); O, which no
label has, is C's left half mirrored.

    python ac4_labels.py <us.iso> <preview.png>
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ac4_cdp import Cdp

W, H = 64, 16
TAG = bytes.fromhex("2080000000000008")
REF_ENTRY = 177                     # a small pack that holds all eight
# the labels in the order the packs keep them (None: call signs, not needed)
WORDS = ("AWACS", "FRIEND", None, None, "ENEMY", "RIGLEY", "RADAR", "FLEET")
NEW = {"FRIEND": "ALIADO", "ENEMY": "ENEMIGO", "FLEET": "FLOTA"}
ENTRIES = list(range(1, 101)) + list(range(176, 187)) + [205] \
    + list(range(259, 291)) + list(range(333, 355))
Y0, Y1 = 1, 10                      # letter rows; the underline is row 13


def unpack(b):
    px = []
    for c in b:
        px += [c & 15, c >> 4]
    return [px[y * W:(y + 1) * W] for y in range(H)]


def pack(img):
    out = bytearray()
    for y in range(H):
        for x in range(0, W, 2):
            out.append(img[y][x] | (img[y][x + 1] << 4))
    return bytes(out)


def ink_runs(img):
    cols = [any(img[y][x] for y in range(Y0, Y1)) for x in range(W)]
    out, x = [], 0
    while x < W:
        if cols[x]:
            s = x
            while x < W and cols[x]:
                x += 1
            out.append((s, x))
        x += 1
    return out


def reference(cdp):
    """the four original label images, in WORDS order, from REF_ENTRY"""
    blob = cdp.get(REF_ENTRY)
    found, at = [], 0
    while True:
        at = blob.find(TAG, at)
        if at < 0:
            break
        img = blob[at + 16:at + 16 + 512]
        if len(img) == 512 and blob[at + 8:at + 16] == bytes(8):
            rows = unpack(img)
            under = sum(1 for v in rows[13] if v)        # the long underline
            if under > 50:
                found.append(img)
        at += 16
    if len(found) != len(WORDS):
        raise SystemExit("labels: expected %d label images in entry %d, found %d"
                         % (len(WORDS), REF_ENTRY, len(found)))
    return {w: img for w, img in zip(WORDS, found) if w}


def glyphs(ref):
    g = {}
    for word, img in ref.items():
        im = unpack(img)
        rs = ink_runs(im)
        # letters that touch (MY, EY, ET): cut the widest run at its faintest
        # column until there is a run per letter
        while 0 < len(rs) < len(word):
            k = max(range(len(rs)), key=lambda i: rs[i][1] - rs[i][0])
            a, b = rs[k]
            if b - a < 10:
                break
            cut = min(range(a + 4, b - 3),
                      key=lambda x: sum(im[y][x] for y in range(Y0, Y1)))
            rs[k:k + 1] = [(a, cut), (cut + 1, b)] if \
                sum(im[y][cut] for y in range(Y0, Y1)) else [(a, cut), (cut, b)]
        if len(rs) != len(word):         # AWACS: too run together; not needed
            continue
        for c, (a, b) in zip(word, rs):
            while b > a + 1 and not any(im[y][b - 1] for y in range(Y0, Y1)):
                b -= 1
            while b > a + 1 and not any(im[y][a] for y in range(Y0, Y1)):
                a += 1
            g.setdefault(c, [row[a:b] for row in im[Y0:Y1]])
    # O: D's round right half mirrored onto the left
    d = g["D"]
    w = len(d[0])
    half = (w + 1) // 2
    g["O"] = [row[w - half:][::-1][:w - half] + row[w - half:] for row in d]
    need = set("".join(NEW.values()))
    if not need <= set(g):
        raise SystemExit("labels: no letters for %s" % "".join(sorted(need - set(g))))
    return g


KERN = {"TA": -3, "AT": -3}            # T's bar over A's slanted side


def compose(base, g, word, gap):
    im = unpack(base)
    for y in range(0, 12):
        for x in range(W):
            im[y][x] = 0
    width = sum(len(g[c][0]) for c in word) + gap * (len(word) - 1) \
        + sum(KERN.get(word[i:i + 2], 0) for i in range(len(word) - 1))
    if width > 60:
        return None
    x = 2 + (60 - width) // 2
    for i, c in enumerate(word):
        for dy, row in enumerate(g[c]):
            for dx, v in enumerate(row):
                if v:
                    im[Y0 + dy][x + dx] = max(v, im[Y0 + dy][x + dx])
        x += len(g[c][0]) + gap + KERN.get(word[i:i + 2], 0)
    return pack(im)


def build(cdp):
    """{original 512 bytes: new 512 bytes}"""
    ref = reference(cdp)
    g = glyphs(ref)
    out = {}
    for en, es in NEW.items():
        new = None
        for gap in (1, 0):
            new = compose(ref[en], g, es, gap)
            if new:
                break
        if not new:
            raise SystemExit("labels: %s does not fit" % es)
        out[ref[en]] = new
    return out


def label_writes(cdp):
    """entry -> [(offset, bytes)] writing the Spanish labels over every copy"""
    new = build(cdp)
    out = {}
    for e in ENTRIES:
        try:
            blob = cdp.get(e)
        except Exception:
            continue
        for old, rep in new.items():
            at = blob.find(old)
            while at >= 0:
                out.setdefault(e, []).append((at, rep))
                at = blob.find(old, at + 1)
    return out


if __name__ == "__main__":
    from PIL import Image
    cdp = Cdp(sys.argv[1])
    ref = reference(cdp)
    new = build(cdp)
    ims = list(ref.values()) + list(new.values())
    sheet = Image.new("L", (W * 4, H * 4 * len(ims)))
    for k, b in enumerate(ims):
        im = Image.new("L", (W, H))
        im.putdata([v * 17 for row in unpack(b) for v in row])
        sheet.paste(im.resize((W * 4, H * 4), Image.NEAREST), (0, k * H * 4))
    sheet.save(sys.argv[2])
    w = label_writes(cdp)
    print("%d entries, %d label copies" % (len(w), sum(len(v) for v in w.values())))
