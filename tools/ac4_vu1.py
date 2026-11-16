"""The VU1 microprograms for tools/vurecomp, rebuilt from the player's own
SLUS_201.52 -- so the download carries no game code.

The game uploads its VU1 microcode from tables inside the executable; a
program as it sits in VU1 micro memory is a few of those pieces laid next to
each other.  The recipe (config/ac4/vu1_recipe.json) records only where each
piece is: (offset in micro memory, offset in the ELF file, length), plus the
manifest fields vurecomp reads (entry points, counts).  No instruction bytes.

    python ac4_vu1.py recipe <out/vu_programs> <SLUS_201.52> <recipe.json>
        (developer) make the recipe from a capture (PS2_VU_CENSUS=1)
    python ac4_vu1.py build <recipe.json> <SLUS_201.52> <dir>
        (player's PC) write <dir>/manifest.json and the program images
"""
import hashlib
import json
import os
import sys

IMAGE = 16384


def _occurrences(elf, pat, limit=4000):
    out, i = [], elf.find(pat)
    while i >= 0 and len(out) < limit:
        out.append(i)
        i = elf.find(pat, i + 1)
    return out


def segments(code, lo, hi, elf):
    """greedy cover of code[lo:hi] by the longest runs found in the ELF"""
    i, segs = lo, []
    while i < hi:
        best_n, best_q = 0, -1
        for q in _occurrences(elf, code[i:i + 8]):
            n = 8
            while i + n < hi and q + n < len(elf) and elf[q + n] == code[i + n]:
                n += 1
            if n > best_n:
                best_n, best_q = n, q
        if best_q < 0:
            raise SystemExit("bytes at VU1 0x%04X are not in the executable" % i)
        segs.append([i, best_q, best_n])
        i += best_n
    return segs


def make_recipe(cap, elf_path, out):
    elf = open(elf_path, "rb").read()
    man = json.load(open(os.path.join(cap, "manifest.json"), encoding="utf-8"))
    progs = []
    for p in man["programs"]:
        img = open(os.path.join(cap, p["image"]), "rb").read()
        lo, hi = p["code_lo"], p["code_hi"]
        meta = {k: v for k, v in p.items() if k != "bytes_"}
        meta["segments"] = segments(img, lo, hi, elf)
        meta["code_sha1"] = hashlib.sha1(img[lo:hi]).hexdigest()
        progs.append(meta)
    rest = {k: v for k, v in man.items() if k != "programs"}
    rest["elf_sha1"] = hashlib.sha1(elf).hexdigest()
    rest["programs"] = progs
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(rest, f, indent=1)
    print("vu1 recipe: %d programs, %d segments" % (len(progs), sum(len(p["segments"]) for p in progs)))


def build(recipe, elf_path, out):
    elf = open(elf_path, "rb").read()
    r = json.load(open(recipe, encoding="utf-8"))
    if hashlib.sha1(elf).hexdigest() != r["elf_sha1"]:
        raise SystemExit("SLUS_201.52 is not the USA executable this recipe was made from")
    os.makedirs(out, exist_ok=True)
    progs = []
    for p in r["programs"]:
        img = bytearray(IMAGE)
        for at, src, n in p["segments"]:
            img[at:at + n] = elf[src:src + n]
        lo, hi = p["code_lo"], p["code_hi"]
        if hashlib.sha1(bytes(img[lo:hi])).hexdigest() != p["code_sha1"]:
            raise SystemExit("could not rebuild %s" % p["image"])
        with open(os.path.join(out, p["image"]), "wb") as f:
            f.write(img)
        progs.append({k: v for k, v in p.items() if k not in ("segments", "code_sha1")})
    man = {k: v for k, v in r.items() if k not in ("programs", "elf_sha1")}
    man["programs"] = progs
    with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(man, f, indent=1)
    print("vu1: %d programs rebuilt from the executable" % len(progs))


if __name__ == "__main__":
    if len(sys.argv) != 5 or sys.argv[1] not in ("recipe", "build"):
        raise SystemExit(__doc__)
    (make_recipe if sys.argv[1] == "recipe" else build)(*sys.argv[2:])
