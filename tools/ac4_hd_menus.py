"""HD menu textures made on the player's PC -- the download carries no image.

Jugar.exe runs the game once, hidden, from boot to the title screen with
PS2_TEXTURE_DUMP set; the runtime saves the textures it draws there.  This
takes the boot sequence's 2D art (the publisher logo, the loading-quote atlas
and what comes before it; see menu_textures), scales each one 4x with our own
filter and writes a texture pack the runtime loads (textures/hd):

    python ac4_hd_menus.py <dump dir> <pack dir> [scale]

The filter: colour scaled premultiplied by alpha (no dark fringes on cut-out
edges) with Lanczos, a light unsharp mask to keep text crisp, alpha scaled
with bicubic and its edge tightened.  Nothing of any other pack is used.
"""
import os
import sys

from PIL import Image, ImageFilter

MIN_SIDE = 128           # small textures (tiles, particles, glyph cells) stay as they are


def read_pam(path):
    d = open(path, "rb").read()
    i = d.index(b"ENDHDR\n") + 7
    head = dict(l.split(" ", 1) for l in d[:i].decode("ascii").splitlines()[1:-1])
    w, h, depth = int(head["WIDTH"]), int(head["HEIGHT"]), int(head["DEPTH"])
    mode = {4: "RGBA", 3: "RGB"}[depth]
    return Image.frombytes(mode, (w, h), d[i:i + w * h * depth]).convert("RGBA")


def write_pam(img, path):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(b"P7\nWIDTH %d\nHEIGHT %d\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"
                % (img.width, img.height))
        f.write(img.tobytes())
    os.replace(tmp, path)


def upscale_fast(img, k):
    """same result for fully opaque textures, without the per-pixel pass"""
    w, h = img.width * k, img.height * k
    rgb = img.convert("RGB").resize((w, h), Image.LANCZOS)
    rgb = rgb.filter(ImageFilter.UnsharpMask(radius=1.6, percent=70, threshold=2))
    out = rgb.convert("RGBA")
    return out


def blank(img):
    """an empty or flat texture: a copy of the screen the game draws effects
    with, never menu art -- scaling it breaks those effects"""
    from PIL import ImageStat
    if img.getchannel("A").getextrema()[1] < 16:
        return True
    return max(ImageStat.Stat(img.convert("RGB")).stddev) < 6


def menu_textures(dump):
    """the textures of the boot sequence, in the order the game drew them,
    up to the loading screen's quote atlas (a 384x1024 or 512x1024 picture
    with text): after it the game preloads the 3D demo (terrain, birds,
    clouds), which must keep its originals"""
    info = os.path.join(dump, "dump_info.tsv")
    names = [l.split("\t")[0] + ".pam" for l in open(info, encoding="ascii") if l.strip()]
    for name in names:
        path = os.path.join(dump, name)
        if not os.path.isfile(path):
            continue
        w, h = map(int, name[:-4].split("_")[1].split("x"))
        if max(w, h) < MIN_SIDE:
            continue
        img = read_pam(path)
        if blank(img):
            continue
        yield name, img
        if (w, h) in ((384, 1024), (512, 1024)):
            return


def main(dump, pack, scale="4"):
    k = int(scale)
    os.makedirs(pack, exist_ok=True)
    made = 0
    for name, img in menu_textures(dump):
        out = os.path.join(pack, name)
        if os.path.exists(out):
            continue
        alpha = img.getchannel("A")
        big = upscale_fast(img, k) if alpha.getextrema() == (255, 255) else upscale_alpha(img, k)
        write_pam(big, out)
        made += 1
    print("hd menus: %d textures scaled %dx into %s" % (made, k, pack))


def upscale_alpha(img, k):
    """premultiplied path with numpy-free channel math (ImageMath-like via point/composite)"""
    from PIL import ImageChops
    w, h = img.width * k, img.height * k
    r, g, b, a = img.split()
    pm = [ImageChops.multiply(c, a) for c in (r, g, b)]          # c * a / 255
    pm = Image.merge("RGB", pm).resize((w, h), Image.LANCZOS)
    pm = pm.filter(ImageFilter.UnsharpMask(radius=1.6, percent=70, threshold=2))
    big_a = a.resize((w, h), Image.BICUBIC)
    big_a = big_a.point(lambda v: 0 if v < 8 else 255 if v > 247 else int((v - 8) * 255 / 239))
    # un-premultiply: c / a * 255, done per channel with a lookup per alpha level
    chans = []
    for c in pm.split():
        cb = c.tobytes()
        ab = big_a.tobytes()
        out = bytes(0 if av == 0 else min(255, cv * 255 // av) for cv, av in zip(cb, ab))
        chans.append(Image.frombytes("L", (w, h), out))
    return Image.merge("RGBA", chans + [big_a])


if __name__ == "__main__":
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    main(*sys.argv[1:4])
