"""Spanish for the text typed at the end of the intro movie (SMV/INTRO.SMV):
    THE USEA CONTINENT/NORTH POINT/         EL CONTINENTE DE USEA/NORTH POINT/
    SEP.19.2004/1405hrs/CLOUDY          ->  19.SEP.2004/1405hrs/NUBLADO

The movie is not changed: the runtime draws pictures over its frames
(ps2_ipu.c, movie overlays).  This makes those pictures from a frame of the
movie with the whole text on it, saved by the runtime itself
(PS2_IPU_DUMP=<dir>:1:1740 and the movie playing, see tools/ac4_playtest.py
and PS2_VFS_ALIAS): the new lines are set with the letters of that frame (B,
which it lacks, is D with a middle bar).  Output, for the mod's movies/:
INTRO.txt and the .pam pictures.

    python ac4_movie_text.py <INTRO_01740.ppm> <out dir> [preview.png]
"""
import os, sys
from PIL import Image

LINES = [  # (y0, y1, English, Spanish, first picture, typed over)
    (188, 206, "THE USEA CONTINENT/NORTH POINT/", "EL CONTINENTE DE USEA/NORTH POINT/", 1667, 29),
    (206, 226, "SEP.19.2004/1405hrs/CLOUDY", "19.SEP.2004/1405hrs/NUBLADO", 1695, 27),
]
LAST = 1761          # the text is on screen until here
X0 = 78              # where the lines start
INK = 60


def runs(px, w, y0, y1):
    cols = [max(px[x, y] for y in range(y0, y1)) > INK for x in range(w)]
    out, x = [], 0
    while x < w:
        if cols[x]:
            s = x
            while x < w and cols[x]:
                x += 1
            out.append((s, x))
        x += 1
    return out


def main():
    frame = Image.open(sys.argv[1]).convert("RGB")
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    grey = frame.convert("L")
    gp = grey.load()
    w = frame.width
    glyphs, gaps, per_line = {}, [], []
    for y0, y1, en, es, _, _ in LINES:
        rs = runs(gp, w, y0, y1)
        letters = en.replace(" ", "")
        if len(rs) != len(letters):
            raise SystemExit("movie text: %d letters on the frame for %r" % (len(rs), en))
        own = {}
        per_line.append(own)
        k = 0
        for word in en.split(" "):
            for i, ch in enumerate(word):
                a, b = rs[k]
                own.setdefault(ch, frame.crop((a, y0, b, y1)))
                glyphs.setdefault(ch, frame.crop((a, y0, b, y1)))
                if i + 1 < len(word):
                    gaps.append(rs[k + 1][0] - b)
                k += 1
    gap = sorted(gaps)[len(gaps) // 2]
    space = 3 * gap + glyphs["I"].width
    if "B" not in glyphs:          # E's three bars over D's round right side,
        src = per_line[1]          # with a notch where the middle bar meets it
        d = src["D"].copy()        # (both from the line B is written on)
        e = src["E"]
        dp, epx = d.load(), e.load()
        el = e.convert("L").load()
        bars = [y for y in range(e.height) if el[e.width // 2, y] > INK]
        mids = [y for y in bars if bars[0] + 2 < y < bars[-1] - 2]
        for y in range(d.height):
            for x in range(min(d.width, e.width)):
                p, q = dp[x, y], epx[x, y]
                dp[x, y] = tuple(max(a, b) for a, b in zip(p, q))
        for y in mids:
            for x in range(d.width - 2, d.width):
                dp[x, y] = (0, 0, 0)
        glyphs["B"] = per_line[1]["B"] = d
    for n, own in enumerate(per_line):              # a line's own letters first
        per_line[n] = dict(glyphs, **own)
    lines_txt = ["# end of the intro: the typed place, date and weather"]
    # a black panel hides the English text the whole time it is on screen
    panel = Image.new("RGBA", (w - X0 + 4 - 40, 226 - 186), (0, 0, 0, 255))
    save_pam(panel, os.path.join(out, "intro_panel.pam"))
    lines_txt.append("%d %d %d %d intro_panel.pam 0 0" % (LINES[0][4], LAST, X0 - 4, 186))
    previews = []
    for n, (y0, y1, en, es, first, typed) in enumerate(LINES):
        width = 0
        items = []
        glyphs_n = per_line[n]
        for word_i, word in enumerate(es.split(" ")):
            for i, ch in enumerate(word):
                items.append(glyphs_n[ch])
                width += glyphs_n[ch].width + (gap if i + 1 < len(word) else 0)
            if word_i + 1 < len(es.split(" ")):
                items.append(None)
                width += space
        img = Image.new("RGBA", (width + 2, y1 - y0), (0, 0, 0, 0))
        x = 0
        words = es.split(" ")
        k = 0
        for word_i, word in enumerate(words):
            for i, ch in enumerate(word):
                g = glyphs_n[ch].convert("L")
                rgba = Image.merge("RGBA", (*glyphs_n[ch].split(), g))
                img.alpha_composite(rgba, (x, 0))
                x += glyphs_n[ch].width + (gap if i + 1 < len(word) else 0)
            if word_i + 1 < len(words):
                x += space
        if X0 + img.width > w - 8:
            raise SystemExit("movie text: %r does not fit" % es)
        name = "intro_line%d.pam" % (n + 1)
        save_pam(img, os.path.join(out, name))
        lines_txt.append("%d %d %d %d %s 0 0 %d" % (first, LAST, X0, y0, name, typed))
        previews.append(img)
    open(os.path.join(out, "INTRO.txt"), "w", newline="\n").write("\n".join(lines_txt) + "\n")
    if len(sys.argv) > 3:
        pv = frame.copy()
        pv.paste((0, 0, 0), (X0 - 4, 186, w - 40, 226))
        for (y0, *_), img in zip(LINES, previews):
            pv.paste(img, (X0, y0), img)
        pv.crop((0, 170, 640, 240)).resize((1280, 140)).save(sys.argv[3])
    print("ok")


def save_pam(img, path):
    with open(path, "wb") as f:
        f.write(b"P7\nWIDTH %d\nHEIGHT %d\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"
                % (img.width, img.height))
        f.write(img.tobytes())


if __name__ == "__main__":
    main()
