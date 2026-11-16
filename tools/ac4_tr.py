"""Our own Spanish (neutral) translation of AC4's disc texts, made from the US
disc only -- no text from any other release is used.

    python ac4_tr.py export <us.iso> <out_dir>
        fuente_en.json: every unique US string with a stable id, its kind
        (radio / text), where it appears and the room its lines have.
    python ac4_tr.py check <out_dir>
        every translation in es/*.json against the English layout.
    python ac4_tr.py build <us.iso> <out_dir> <mod dir> [hud_part0.bin]
        the mod's BIN/DATA.CDP.<entry>.PARTS (overlay format, see ac4_es.py).

Translations live in <out_dir>/es/*.json as {"<id>": "<Spanish>"}.  A Spanish
string must have exactly as many lines as the English one: the game finds
texts by LINE number (menu help, briefing pieces and subtitles are timed and
addressed by line), so one extra line shifts every later text.
"""
import glob, json, os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ac4_text as T
import ac4_es as E
from ac4_cdp import Cdp


def find_tables(cdp):
    """[(entry, path, radio, [strings])] for every text table of the US disc"""
    out = []
    for i, (sec, size) in enumerate(cdp.entries):
        if not size or size > 64 << 20:
            continue
        blob = cdp.get(i)
        found = []
        T.walk(blob, [], found)
        for path, strings in found:
            part = E.part_at(blob, path)
            radio = T.parse_radio(part) is not None
            out.append((i, path, radio, [T.decode(s) for s in strings]))
    return out


def kind_of(entry, radio):
    if radio:
        return "radio"
    if 176 <= entry <= 186:
        return "menu"
    if entry == 205:
        return "hangar"
    if 259 <= entry <= 271:
        return "historia"
    if 272 <= entry <= 289:
        return "briefing"
    if 333 <= entry <= 354:
        return "resultados"
    return "texto"


def export(iso, out_dir):
    cdp = Cdp(iso)
    tables = find_tables(cdp)
    ids, items = {}, []
    for entry, path, radio, strings in tables:
        for k, s in enumerate(strings):
            if s not in ids:
                ids[s] = len(items)
                items.append({"id": len(items), "en": s, "kind": kind_of(entry, radio),
                              "where": []})
            items[ids[s]]["where"].append([entry, path, k])
    os.makedirs(out_dir, exist_ok=True)
    json.dump({"note": "US disc strings; ids are stable for this disc",
               "strings": items}, open(os.path.join(out_dir, "fuente_en.json"), "w",
                                       encoding="utf-8"), ensure_ascii=False, indent=1)
    print("%d tables, %d unique strings" % (len(tables), len(items)))


def load(out_dir):
    src = json.load(open(os.path.join(out_dir, "fuente_en.json"), encoding="utf-8"))["strings"]
    es = {}
    for f in sorted(glob.glob(os.path.join(out_dir, "es", "*.json"))):
        for k, v in json.load(open(f, encoding="utf-8")).items():
            if k.startswith("_"):
                continue
            es[int(k)] = v
    return src, es


def glyphs(line):
    """characters on screen: a {BUTTON} or {XXXX} code is one glyph"""
    import re
    return len(re.sub(r"\{[^}]*\}", "#", line))


def widest(src):
    """kind -> the longest English line of that kind: texts of one kind share
    one box on screen, so that is the room a Spanish line has"""
    w = {}
    for it in src:
        for l in it["en"].split("\n"):
            w[it["kind"]] = max(w.get(it["kind"], 0), glyphs(l))
    # a few English radio lines run long with no break (121 characters);
    # the usual ones stay under ~40, so Spanish radio lines keep to 44
    w["radio"] = 44
    return w


def problems(item, es, room=None):
    """layout problems of one translation"""
    p = []
    en = item["en"]
    if es.count("\n") != en.count("\n"):
        p.append("lines %d != %d" % (es.count("\n") + 1, en.count("\n") + 1))
    try:
        T.encode(es)
    except Exception as ex:
        p.append(str(ex))
    if room is None:
        room = max(glyphs(l) for l in en.split("\n"))
    for l in es.split("\n"):
        if glyphs(l) > room:
            p.append("long line %d > %d: %r" % (glyphs(l), room, l))
    for tok in ("{SQUARE}", "{CROSS}", "{TRIANGLE}", "{CIRCLE}"):
        if en.count(tok) != es.count(tok):
            p.append("button %s" % tok)
    return p


def check(out_dir):
    src, es = load(out_dir)
    missing = [it["id"] for it in src if it["id"] not in es]
    room = widest(src)
    bad = 0
    for it in src:
        if it["id"] in es:
            pr = problems(it, es[it["id"]], room[it["kind"]])
            if pr:
                bad += 1
                print(it["id"], it["kind"], "; ".join(pr))
    print("%d strings, %d translated, %d missing, %d with problems"
          % (len(src), len(src) - len(missing), len(missing), bad))
    if missing:
        print("first missing:", missing[:20])
    return bad == 0


def fit(item, es):
    """a translation the game can take: same number of lines as the English
    (extra lines joined, missing ones added empty), only encodable glyphs"""
    want = item["en"].count("\n") + 1
    lines = es.split("\n")
    while len(lines) > want:
        k = min(range(len(lines) - 1), key=lambda i: len(lines[i]) + len(lines[i + 1]))
        lines[k:k + 2] = [lines[k].rstrip() + " " + lines[k + 1].lstrip()]
    while len(lines) < want:
        lines.append("")
    s = "\n".join(lines)
    T.encode(s)
    return s


def encode_like(old, radio, strings):
    """the table re-encoded with the same header and terminators as the US one"""
    header = struct.unpack_from("<I", old, 0)[0]
    words = []
    if radio:
        for s in strings:
            words += [T.RADIO_NL if v == T.NL else v for v in T.encode(s)] + [T.NL]
        words.append(T.NL)
    else:
        for s in strings:
            words += T.encode(s) + [T.END]
        words.append(T.NL)
    blob = struct.pack("<I", header) + struct.pack("<%dH" % len(words), *words)
    while len(blob) % 16:
        blob += b"\xff\xff" if (16 - len(blob) % 16) >= 2 else b"\xff"
    return blob


def build(iso, out_dir, mod_dir, hud_file=None):
    cdp = Cdp(iso)
    src, es = load(out_dir)
    by_text = {it["en"]: it for it in src}
    tables = find_tables(cdp)
    hud = E.hud_parts(cdp, hud_file) if hud_file else {}
    import ac4_strips
    strips = ac4_strips.strip_parts(cdp)        # replay / demo text images
    import ac4_labels
    labels = ac4_labels.label_writes(cdp)       # radio speaker labels
    import ac4_title                            # the title's PRESS START line
    import ac4_cards                            # the cards before each briefing
    for mod in (ac4_title.title_writes(cdp), ac4_cards.card_writes(cdp)):
        for e, w in mod.items():
            labels.setdefault(e, []).extend(w)
    per_entry = {}
    for entry, path, radio, strings in tables:
        per_entry.setdefault(entry, []).append((path, radio, strings))
    for e in list(hud) + list(strips) + list(labels):
        per_entry.setdefault(e, [])
    files = os.path.join(mod_dir, "files", "BIN")
    os.makedirs(files, exist_ok=True)
    for f in glob.glob(os.path.join(files, "DATA.CDP.*")):
        os.remove(f)
    done = kept = 0
    for e, ts in sorted(per_entry.items()):
        blob = cdp.get(e)
        parts = []
        if e in hud:
            parts.append(([0], hud[e]))
        parts += strips.get(e, [])
        for path, radio, strings in ts:
            new, changed = [], False
            for s in strings:
                it = by_text[s]
                tr = es.get(it["id"])
                if tr is None or tr == s:
                    new.append(s); kept += tr is None
                    continue
                new.append(fit(it, tr)); changed = True; done += 1
            if changed:
                parts.append((path, encode_like(E.part_at(blob, path), radio, new)))
        if not parts and e not in labels:
            continue
        size, writes = E.overlay(blob, parts) if parts else (len(blob), [])
        writes += labels.get(e, [])             # same size, at their own place
        if e == 272:
            import ac4_free_mission
            rebuilt = bytearray(blob)
            rebuilt.extend(bytes(max(0, size - len(rebuilt))))
            for at, data in writes:
                rebuilt[at:at + len(data)] = data
            repaired = ac4_free_mission.repair_resource(bytes(rebuilt), cdp.get(177))
            size, writes = len(repaired), [(0, repaired)]
        E.write_overlay(os.path.join(files, "DATA.CDP.%04d.PARTS" % e), size, writes)
    # pictures drawn over the movies (ps2_ipu.c), made by ac4_movie_text.py
    movies = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "localization",
                          "es-419", "movies")
    if os.path.isdir(movies):
        import shutil
        dst = os.path.join(mod_dir, "movies")
        shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(movies, dst)
    print("%d texts translated in place, %d left in English" % (done, kept))


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "export":
        export(sys.argv[2], sys.argv[3])
    elif cmd == "check":
        sys.exit(0 if check(sys.argv[2]) else 1)
    elif cmd == "build":
        build(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5] if len(sys.argv) > 5 else None)
