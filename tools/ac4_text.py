"""Texts of Ace Combat 04 (DATA.CDP): decode, encode and catalogue.

AC4 stores its proportional-font texts (radio subtitles, briefings, story
subtitles, menu help, results) as tables of 16-bit glyph codes inside the
nested packs of BIN/DATA.CDP:

    u32 count, then `count` strings, each ended by 0xFFFE
    0x000D space         0xFFFF line break
    'A'..'Z'  -> code - 0x1A  (0x27..0x40)
    'a'..'z'  -> code - 0x20  (0x41..0x5A)
    '0'..'9'  -> code - 0x13  (0x1D..0x26)
    punctuation and the rest: CODES below (learnt from the US and PAL discs)

    python ac4_text.py scan <iso> <out.json>     every table, deduplicated
"""
import json, os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

END, NL, SP = 0xFFFE, 0xFFFF, 0x000D

# code -> text for everything that is not a letter or a digit.  Codes not
# listed here are kept as {XXXX} so nothing is lost on a round trip.
CODES = {
    0xA4: ",", 0xA5: ".", 0xA6: ":", 0xA7: "\u2014", 0xA8: "?", 0xA9: "!",
    0x00: "<", 0x01: ">", 0xAB: "#",
    0xAC: "/", 0xAF: "'", 0xB0: '"', 0xB1: "(", 0x0E: ")", 0x12: "-", 0x18: "%",
    # controller buttons, drawn as icons
    0x08: "{SQUARE}", 0x09: "{CROSS}", 0x0A: "{TRIANGLE}", 0x0B: "{CIRCLE}",
    0xA3: "¡",                     # inverted exclamation mark
}
# The US subtitle font (180 glyphs, in the executable at 0x2B1C70) already
# holds the Latin-1 letters: glyph = Latin-1 code - 0x64 for U+00BF..U+00FF
# (inverted question mark 5B, A-acute 5D, n-tilde 8D, u-diaeresis 98...),
# the same codes the PAL Spanish text uses.
for _cp in range(0xBF, 0x100):
    CODES.setdefault(_cp - 0x64, chr(_cp))


def decode_char(v):
    if v == SP:
        return " "
    if v == NL:
        return "\n"
    if 0x27 <= v <= 0x40:
        return chr(v + 0x1A)
    if 0x41 <= v <= 0x5A:
        return chr(v + 0x20)
    if 0x1D <= v <= 0x26:
        return chr(v + 0x13)
    if v in CODES:
        return CODES[v]
    return "{%04X}" % v


REV = {t: c for c, t in CODES.items()}


def encode(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "{":
            j = s.index("}", i)
            tok = s[i:j + 1]
            out.append(REV[tok] if tok in REV else int(tok[1:-1], 16))
            i = j + 1
            continue
        if c == " ":
            out.append(SP)
        elif c == "\n":
            out.append(NL)
        elif "A" <= c <= "Z":
            out.append(ord(c) - 0x1A)
        elif "a" <= c <= "z":
            out.append(ord(c) - 0x20)
        elif "0" <= c <= "9":
            out.append(ord(c) - 0x13)
        elif c in REV:
            out.append(REV[c])
        else:
            raise ValueError("no glyph code for %r" % c)
        i += 1
    return out


def decode(words):
    return "".join(decode_char(v) for v in words)


RADIO_NL = 0xFFFD


def parse_radio(blob):
    """Radio subtitles (in each mission pack): u32 count, then `count` lines
    ended by 0xFFFF, with 0xFFFD as the line break inside a subtitle."""
    n = struct.unpack_from("<I", blob, 0)[0]
    w = struct.unpack_from("<%dH" % ((len(blob) - 4) // 2), blob, 4)
    if END in w:
        return None
    strings, cur, letters = [], [], 0
    for v in w:
        if v == NL:
            strings.append([NL if x == RADIO_NL else x for x in cur]); cur = []
            if len(strings) == n:
                break
            continue
        if v > 0x1000 and v != RADIO_NL:
            return None
        if 0x27 <= v <= 0x5A:
            letters += 1
        cur.append(v)
    if len(strings) != n or letters < 2 * n:
        return None
    return strings


def parse_table(blob):
    """A text table, or None: count, then END-terminated strings that use
    plausible glyph codes (or the radio layout, see parse_radio)."""
    if len(blob) < 8:
        return None
    n = struct.unpack_from("<I", blob, 0)[0]
    if not 1 <= n <= 4096:
        return None
    r = parse_radio(blob)
    if r is not None:
        return r
    # the u32 is not the number of strings (19 in a table of 7): keep it as
    # it is and read END-terminated strings up to the trailing padding
    w = struct.unpack_from("<%dH" % ((len(blob) - 4) // 2), blob, 4)
    strings, cur, letters = [], [], 0
    for v in w:
        if v == END:
            strings.append(cur); cur = []
            continue
        if v > 0x1000 and v != NL:
            return None
        if 0x27 <= v <= 0x5A:
            letters += 1
        cur.append(v)
    # after the last string: padding (US 0xFFFF, PAL zeros and loose "null"
    # lines), or -- in the PAL briefings -- a last string with no terminator
    tail = list(cur)
    while tail and tail[-1] in (NL, 0):
        tail.pop()
    if decode([v for v in tail if v not in (NL, 0)]).replace("null", "").strip():
        if any(v == 0 for v in tail):
            return None
        strings.append(tail)
        if strings[-1] and any(0x27 <= v <= 0x5A for v in tail):
            letters += sum(1 for v in tail if 0x27 <= v <= 0x5A)
    if not strings or letters < 4 * len(strings):
        return None
    return strings


def walk(blob, path, out, depth=0):
    """Every text table in a nested pack (u32 count, u32 offsets...)."""
    t = parse_table(blob)
    if t is not None:
        out.append((path, t))
        return
    if depth > 6 or len(blob) < 8:
        return
    n = struct.unpack_from("<I", blob, 0)[0]
    if not 1 <= n <= 256 or 4 + 4 * n > len(blob):
        return
    offs = list(struct.unpack_from("<%dI" % n, blob, 4))
    if any(o > len(blob) for o in offs):
        return
    # empty parts have offset 0; a part ends where the next larger one starts
    starts = sorted(set(o for o in offs if o >= 4 + 4 * n))
    if not starts:
        return
    for k, o in enumerate(offs):
        if o < 4 + 4 * n:
            continue
        later = [s for s in starts if s > o]
        end = later[0] if later else len(blob)
        walk(blob[o:end], path + [k], out, depth + 1)


def scan(iso_path):
    from ac4_cdp import Cdp
    cdp = Cdp(iso_path)
    tables = []
    for i, (sec, size) in enumerate(cdp.entries):
        if not size or size > 64 << 20:
            continue
        found = []
        walk(cdp.get(i), [], found)
        for path, strings in found:
            tables.append({"entry": i, "path": path, "strings": [decode(s) for s in strings]})
    return tables


if __name__ == "__main__":
    if sys.argv[1] == "scan":
        tables = scan(sys.argv[2])
        uniq = {}
        for t in tables:
            for k, s in enumerate(t["strings"]):
                uniq.setdefault(s, []).append([t["entry"], t["path"], k])
        unknown = {}
        for s in uniq:
            for part in s.split("{")[1:]:
                tok = part.split("}")[0]
                if "{" + tok + "}" not in REV:
                    unknown[tok] = unknown.get(tok, 0) + 1
        json.dump({"tables": len(tables), "unknown_codes": unknown,
                   "strings": [{"text": s, "where": w} for s, w in uniq.items()]},
                  open(sys.argv[3], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print("%d tables, %d unique strings, %d unknown codes" % (len(tables), len(uniq), len(unknown)))
        for c, n in sorted(unknown.items(), key=lambda x: -x[1])[:40]:
            print("  {%s} x%d" % (c, n))
