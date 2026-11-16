"""Shared helpers for the Spanish mod, all made from the US disc and the US
executable: nested-pack access, the overlay patch format, the HUD labels and
the executable's block-font texts.  The texts themselves are our own
translation (tools/ac4_tr.py, localization/es-419-propio); nothing here reads
any other release of the game.

    python ac4_es.py elf <elf_strings.json> <ejecutable.json> <out script.lua>
"""
import json, os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def part_at(blob, path):
    for k in path:
        n = struct.unpack_from("<I", blob, 0)[0]
        offs = list(struct.unpack_from("<%dI" % n, blob, 4))
        o = offs[k]
        starts = sorted(set(x for x in offs if x >= 4 + 4 * n))
        later = [s for s in starts if s > o]
        blob = blob[o:(later[0] if later else len(blob))]
    return blob


def locate(blob, path):
    """(absolute start of the innermost pack, absolute address of the offset
    word that points at the part, absolute start of the part, its room)"""
    base, end = 0, len(blob)
    for depth, k in enumerate(path):
        n = struct.unpack_from("<I", blob, base)[0]
        offs = list(struct.unpack_from("<%dI" % n, blob, base + 4))
        o = offs[k]
        starts = sorted(set(x for x in offs if x >= 4 + 4 * n))
        later = [s for s in starts if s > o]
        pend = base + later[0] if later else end
        if depth == len(path) - 1:
            return base, base + 4 + 4 * k, base + o, pend - (base + o)
        base, end = base + o, pend
    raise ValueError("empty path")


HUD_ROWS = (94, 112)            # the label boxes of the HUD atlas (see ac4_hud.py)


def hud_parts(us, hud_file):
    """entry -> new part [0] for the mission packs whose HUD atlas has the
    English label boxes: only the rows of the boxes are taken from hud_file
    (made by ac4_hud.py from the US atlas's own font), so a pack whose atlas
    differs elsewhere keeps the rest of its own"""
    import ac4_hud as H
    new = open(hud_file, "rb").read()
    ref = part_at(us.get(1), [0])
    spans = [tuple(H.DATA_OFF + y * H.W // 2 for y in rows)
             for rows in [HUD_ROWS] + list(H.WORD_ROWS)]
    out = {}
    for e in range(1, 101):
        try:
            old = part_at(us.get(e), [0])
        except Exception:
            continue
        if old[:H.DATA_OFF] != ref[:H.DATA_OFF]:
            continue
        cur, changed = old, False
        for lo, hi in spans:          # only the rows this pack has like the US one
            if len(old) >= hi and old[lo:hi] == ref[lo:hi]:
                cur = cur[:lo] + new[lo:hi] + cur[hi:]
                changed = True
        if changed:
            out[e] = cur
    return out


def overlay(blob, parts):
    """(final size, [(at, bytes)]) for the entry with these parts replaced
    WITHOUT moving anything: a part that fits its room is written in place,
    a larger one goes after the end of the entry and only the offset word
    that points at it changes.  The game reads some data at fixed places
    (the menu help pack 177 keeps bytes right after its text that colour the
    hangar models; shifting them painted the aircraft red), so every byte the
    US game had stays at its address."""
    writes, tail = [], bytearray()
    start = (len(blob) + 15) & ~15
    for path, data in parts:
        pack, slot, at, room = locate(blob, path)
        if len(data) <= room:
            writes.append((at, data))
            continue
        while len(tail) % 16:
            tail += b"\xff"
        new = start + len(tail)
        tail += data
        writes.append((slot, struct.pack("<I", new - pack)))
    if tail:
        writes.append((start, bytes(tail)))
        return start + len(tail), writes
    return len(blob), writes


def write_overlay(path, final_size, writes):
    """BIN/DATA.CDP.<entry>.PARTS version 2: the original entry grown to
    final_size with these byte ranges written over it (see ps2_vfs.c)"""
    out = bytearray(b"AC4P" + struct.pack("<III", 2, final_size, len(writes)))
    for at, data in writes:
        out += struct.pack("<II", at, len(data)) + data
        while len(out) % 4:
            out += b"\0"
    open(path, "wb").write(out)


# Texts too long for their own room: written into free bytes and the menu
# entries pointing at the original are pointed at the copy.  The pause menu
# said REPETIR twice (REPLAY and RETRY); each is now its own word.
# Free bytes: most zero gaps between texts are really variables (the one after
# "PAUSE" changes during play).  The gap after "PFS" (the HDD file system,
# unused without a hard disk) stayed zero through menus, flight and the pause
# menu, and nothing points into 00411614..00411657.
#   (old text address, original text, new address, room there, Spanish,
#    pointers to redirect)
RELOCATE = [
    (0x00410B50, "RETRY", 0x00411620, 19, "REINTENTAR MISION", [0x002B7E88, 0x002B7FC8]),
    # REPLAY's 16 bytes up to "HUD", which take RETRY's old place: only once
    # RETRY has moved (the script checks no listed pointer still lands there)
    (0x00410B48, "REPLAY", 0x00410B48, 15, "REPRODUCIR", [0x002B7E60]),
]


def elf_script(strings_json, table_json, out_lua):
    """script.lua for the executable's block-font texts: each translation is
    written over the original in place (it must fit the original's room:
    length plus the zero padding before the next string), after checking that
    the original bytes are there -- a different executable is left alone."""
    strings = json.load(open(strings_json, encoding="utf-8"))
    table = {k: v for k, v in json.load(open(table_json, encoding="utf-8")).items()
             if not k.startswith("_")}
    rows, misfit = [], []
    for s in strings:
        es = table.get(s["text"])
        if es is None or es == s["text"]:
            continue
        if es.count("\n") != s["text"].count("\n"):
            misfit.append((s["text"], es, "lines"))
            continue
        if any(ord(c) > 126 or (ord(c) < 32 and c != "\n") for c in es):
            misfit.append((s["text"], es, "non-ASCII"))
            continue
        if len(es) > s["room"]:
            misfit.append((s["text"], es, "%d > %d" % (len(es), s["room"])))
            continue
        if any(int(s["addr"], 16) == r[0] for r in RELOCATE):
            continue                            # done by the relocation below
        rows.append((int(s["addr"], 16), s["text"], es, s["room"]))
    def lua(x):
        return '"' + x.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'
    with open(out_lua, "w", encoding="utf-8", newline="\n") as f:
        f.write("-- Textos del ejecutable (menus, HUD, titulos de mision) en espanol.\n"
                "-- Generado por tools/ac4_es.py desde localization/es-419/ejecutable.json.\n"
                "-- Cada texto se escribe sobre el original, que debe estar intacto.\n"
                "local T = {\n")
        for a, en, es, room in rows:
            f.write("  {0x%08X, %s, %s, %d},\n" % (a, lua(en), lua(es), room))
        f.write("}\n"
                "local done, skipped = 0, 0\n"
                "for _, e in ipairs(T) do\n"
                "  local addr, en, es, room = e[1], e[2], e[3], e[4]\n"
                "  if ac4.read_bytes(addr, #en + 1) == en .. \"\\0\" then\n"
                "    ac4.write_bytes(addr, es .. string.rep(\"\\0\", room + 1 - #es))\n"
                "    done = done + 1\n"
                "  else\n"
                "    skipped = skipped + 1\n"
                "  end\n"
                "end\n")
        # relocated texts: all checks first (originals and pointers intact, the
        # new place zero), then the string, then the pointers
        f.write("-- Textos que no caben en su lugar: se copian a bytes libres y los\n"
                "-- punteros del menu se redirigen.\n"
                "local R = {\n")
        for old, en, new, room, es, ptrs in RELOCATE:
            assert len(es) <= room and all(ord(c) < 127 for c in es)
            f.write("  {0x%08X, %s, 0x%08X, %d, %s, {%s}},\n"
                    % (old, lua(en), new, room, lua(es), ", ".join("0x%08X" % p for p in ptrs)))
        f.write("}\n"
                "local moved = 0\n"
                "for _, e in ipairs(R) do\n"
                "  local old, en, new, room, es, ptrs = e[1], e[2], e[3], e[4], e[5], e[6]\n"
                "  local ok = ac4.read_bytes(old, #en + 1) == en .. \"\\0\"\n"
                "  for _, p in ipairs(ptrs) do ok = ok and ac4.read32(p) == old end\n"
                "  if new ~= old then ok = ok and ac4.read_bytes(new, room + 1) == string.rep(\"\\0\", room + 1) end\n"
                "  for _, o in ipairs(R) do\n"
                "    if o ~= e then\n"
                "      for _, p in ipairs(o[6]) do\n"
                "        local v = ac4.read32(p)\n"
                "        if v >= new and v <= new + room then ok = false end\n"
                "      end\n"
                "    end\n"
                "  end\n"
                "  if ok then\n"
                "    ac4.write_bytes(new, es .. string.rep(\"\\0\", room + 1 - #es))\n"
                "    for _, p in ipairs(ptrs) do ac4.write32(p, new) end\n"
                "    moved = moved + 1\n"
                "  else\n"
                "    skipped = skipped + 1\n"
                "  end\n"
                "end\n"
                "ac4.log(string.format(\"espanol: %d textos del ejecutable traducidos, %d reubicados, %d omitidos\", done, moved, skipped))\n")
    print("%d texts written to %s, %d do not fit:" % (len(rows), out_lua, len(misfit)))
    for m in misfit:
        print("   ", m)


if __name__ == "__main__":
    if len(sys.argv) == 5 and sys.argv[1] == "elf":
        elf_script(sys.argv[2], sys.argv[3], sys.argv[4])
    else:
        print(__doc__)
        sys.exit(1)
