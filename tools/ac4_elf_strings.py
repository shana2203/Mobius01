"""Strings of the US executable that the game draws with its block fonts
(menus, HUD messages, mission titles and objectives), with the room each has:
its length plus the zero bytes up to the next string.
    python ac4_elf_strings.py <SLUS_201.52> <out.json>"""
import json, re, sys

ELF_DELTA = 0x100000 - 0x1000          # vaddr = file offset + this (one PT_LOAD)
RANGES = [(0x403700, 0x40D7D8), (0x410480, 0x41172C)]   # .rodata, .sdata

# not shown on screen: file names, format strings for the log, library tags
SKIP = re.compile(r"(\.(irx|img|cdp|tbl|smv|vpk|elf|bin|ico|sys)\b|cdrom|host0|mc0|rom0|"
                  r"%[0-9]*[sdxXcuf]|\\|\bsce|libsd|SLUS|BISLPS|HDD0_|type_info|"
                  r"^[\W_]+$)", re.I)
LOWER_ONLY = re.compile(r"^[a-z_0-9]+$")       # symbol-like, never shown


def main():
    b = open(sys.argv[1], "rb").read()
    out = []
    for lo, hi in RANGES:
        o, end = lo - ELF_DELTA, hi - ELF_DELTA
        while o < end:
            if 32 <= b[o] < 127 and (o == 0 or b[o - 1] == 0):
                e = o
                # a line break inside a text is part of it (mission objectives
                # are several lines: "...CONTINUE TO DESTROY\nENEMY FORCES.")
                while e < end and (32 <= b[e] < 127 or b[e] == 10):
                    e += 1
                if e < end and b[e] == 0 and e - o >= 2:
                    s = b[o:e].decode("ascii")
                    z = e
                    while z < end and b[z] == 0:
                        z += 1
                    room = z - o - 1                     # keep one terminator
                    letters = sum(c.isalpha() for c in s)
                    if letters >= 2 and not SKIP.search(s) and not LOWER_ONLY.match(s) \
                            and not re.match(r"^[A-Za-z]{2}[^A-Za-z ]$", s):
                        out.append({"addr": "0x%08X" % (o + ELF_DELTA), "text": s,
                                    "len": len(s), "room": room})
                o = e
            o += 1
    json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(len(out), "strings")


if __name__ == "__main__":
    main()
