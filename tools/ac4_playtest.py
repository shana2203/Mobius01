"""Hidden, silent play-through tests of the real game with screenshots compared
against reference images (the "red hangar planes" kind of bug: game data the
mod changes, which the GS-capture regression in tools/regression cannot see).

Every run works on COPIES of the player's saves and settings in a temporary
folder: windowed-hidden, no audio, no vibration.  The player's own files are
never written.

    python -I ac4_playtest.py run      [--case NAME] [--mods installed|none|<dir>]
    python -I ac4_playtest.py update   [--case NAME]   (take new references)
    python -I ac4_playtest.py calibrate --case NAME --every 50   (find frames)

Cases live in tools/regression/playtest.json.  References are game images
(Namco's), so they are kept OUT of the source tree: in
AC4Recomp-desarrollo/pruebas/referencias (never packaged).
"""
import argparse, json, os, re, shutil, subprocess, sys, tempfile, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE.parent                                     # VulkanSource
DEV = SRC.parent                                      # AC4Recomp-desarrollo
ROOT = DEV.parent / "AC4Recomp"                       # the installed game
RT = ROOT / "VulkanRuntime" / "runtime"
ISO = Path(os.environ.get("AC4_ISO", DEV.parent / "Ace Combat 04 SK.iso"))
CASES = HERE / "regression" / "playtest.json"
REFS = DEV / "pruebas" / "referencias"
OUT = DEV / "pruebas" / "resultados"
THUMB = (480, 360)


def settings_copy(dst):
    txt = (RT / "ac4_settings.ini").read_text(encoding="utf-8")
    for k, v in (("window_mode", "0"), ("vibration", "0"), ("show_fps", "0")):
        txt = re.sub(r"(?m)^(%s\s*=\s*)\S+" % re.escape(k), r"\g<1>%s" % v, txt)
    Path(dst).write_text(txt, encoding="utf-8")


def mods_dir(spec):
    if spec == "none":
        return None
    if spec == "installed":
        return ROOT / "VulkanRuntime" / "mods"
    return Path(spec)


def play(case, mods, shots, every=0, exe=None, timeout=900, extra_env=None):
    """run the game for one case; returns {frame: PIL image}"""
    from PIL import Image
    run = Path(tempfile.mkdtemp(prefix="ac4pt_"))
    try:
        (run / "out").mkdir()
        # a frozen test save (pruebas/partida) when there is one: the player's
        # own saves move on as they play and the scripts stop matching
        saves = DEV / "pruebas" / "partida"
        if not any((saves / name).is_file() for name in ("card0.ps2", "card0.ps2mc")):
            saves = ROOT / "VulkanRuntime" / "saves"
        shutil.copytree(saves, run / "saves",
                        ignore=shutil.ignore_patterns("*.lock", "respaldo"))
        settings_copy(run / "ac4_settings.ini")
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith("PS2_")}
        env.update({
            "PS2_SHADER_DIR": str(RT / "shaders"), "PS2_SAVE_DIR": str(run / "saves"),
            "PS2_SETTINGS_FILE": str(run / "ac4_settings.ini"),
            "PS2_TEXTURE_PACK": str(RT / "textures" / "hd"),
            "SDL_AUDIO_DRIVER": "dummy", "PS2_PIPELINE_CACHE": str(run / "ac4_pipelines"),
            "PS2_ASYNC_GFX": "1", "PS2_ASYNC_FIELDS": "3", "PS2_GPU_FRAMES": "3",
            "PS2_LOCKSTEP": "0", "PS2_LATE_INPUT": "0", "SDL_JOYSTICK_THREAD": "1",
            "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS": "0",
            "DISABLE_RTSS_LAYER": "1", "DISABLE_LAYER_NV_PRESENT_1": "1",
            "PS2_PAD_SCRIPT": case["script"],
        })
        for ext in (".cache", ".keys"):
            p = RT / ("ac4_pipelines" + ext)
            if p.exists():
                shutil.copy(p, run / ("ac4_pipelines" + ext))
        m = mods_dir(mods)
        if m is None:
            env["PS2_NO_MODS"] = "1"
        else:
            env["PS2_MOD_DIR"] = str(m)
        if every:
            env["PS2_SHOT_EVERY"] = str(every)
            env["PS2_SHOT_FROM"] = str(case.get("from", 0))
        else:
            env["PS2_SHOT_AT"] = ",".join(str(f) for f in shots)
        env.update(extra_env or {})        # e.g. PS2_SPEED_LOG for profiling
        last = max(shots) if shots else case["vblanks"]
        cmd = [str(exe or RT / "ac4.exe"), "--data", str(RT), "--disc", str(ISO), "--hidden",
               "--vblanks", str(max(case["vblanks"], last + 60)), "--watchdog", "0"]
        with open(run / "log.txt", "w", encoding="utf-8", errors="replace") as lf:
            p = subprocess.Popen(cmd, cwd=run, env=env, stdout=lf, stderr=subprocess.STDOUT)
            try:
                rc = p.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                p.kill()
                raise
        if rc != 0 and not list((run / "out").glob("shot_*.ppm")):
            raise SystemExit("el juego no arranco (codigo %d = 0x%08X); si es un exe de "
                             "build-pgo, falta C:\\msys64\\ucrt64\\bin en el PATH"
                             % (rc, rc & 0xFFFFFFFF))
        imgs = {}
        for f in sorted((run / "out").glob("shot_*.ppm")):
            im = Image.open(f).convert("RGB")
            im.thumbnail(THUMB)
            imgs[int(f.stem.split("_")[1])] = im.copy()
        return imgs, (run / "log.txt").read_text(encoding="utf-8", errors="replace")
    finally:
        shutil.rmtree(run, ignore_errors=True)


def diff(a, b, box=None, metric="changed"):
    """over box (fractions of the image):
    changed - share of pixels whose largest channel moved more than 48: a
              changed word is a few percent of the pixels, which a mean hides;
    tint    - change of the mean red-minus-green: an aircraft painted red;
    mean    - mean absolute difference per channel (0..255)."""
    from PIL import ImageChops, ImageStat
    if a.size != b.size:
        b = b.resize(a.size)
    if box:
        w, h = a.size
        r = (int(box[0] * w), int(box[1] * h), int(box[2] * w), int(box[3] * h))
        a, b = a.crop(r), b.crop(r)
    if metric == "tint":
        ma, mb = ImageStat.Stat(a).mean, ImageStat.Stat(b).mean
        return abs((ma[0] - ma[1]) - (mb[0] - mb[1]))
    d = ImageChops.difference(a, b)
    if metric == "mean":
        return sum(ImageStat.Stat(d).mean) / 3.0
    r, g, bl = d.split()
    m = ImageChops.lighter(ImageChops.lighter(r, g), bl)
    hist = m.histogram()
    return 100.0 * sum(hist[49:]) / max(1, sum(hist))


def sheet(rows, path):
    from PIL import Image, ImageDraw
    w, h = THUMB
    im = Image.new("RGB", (w * 2 + 10, (h + 18) * len(rows)), (30, 30, 30))
    d = ImageDraw.Draw(im)
    for i, (label, a, b) in enumerate(rows):
        y = i * (h + 18)
        d.text((4, y + 2), label, fill=(255, 255, 0))
        if a: im.paste(a.resize(THUMB), (0, y + 16))
        if b: im.paste(b.resize(THUMB), (w + 10, y + 16))
    im.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["run", "update", "calibrate"])
    ap.add_argument("--case")
    ap.add_argument("--mods", default="installed")
    ap.add_argument("--exe")
    ap.add_argument("--every", type=int, default=50)
    a = ap.parse_args()
    cases = json.loads(CASES.read_text(encoding="utf-8"))["cases"]
    if a.case:
        cases = [c for c in cases if c["name"] == a.case]
        if not cases:
            sys.exit("no such case")
    OUT.mkdir(parents=True, exist_ok=True)
    if a.cmd == "calibrate":
        c = cases[0]
        imgs, _ = play(c, a.mods, [], every=a.every, exe=a.exe)
        d = OUT / ("calibrar_" + c["name"])
        shutil.rmtree(d, ignore_errors=True); d.mkdir()
        for f, im in imgs.items():
            im.save(d / ("%06d.png" % f))
        print("%d frames in %s" % (len(imgs), d))
        return
    ok_all, summary = True, []
    for c in cases:
        frames = [s["frame"] for s in c["shots"]]
        t0 = time.time()
        imgs, log = play(c, c.get("mods", a.mods) if a.mods == "installed" else a.mods,
                         frames, exe=a.exe)
        rows = []
        refdir = REFS / c["name"]
        for s in c["shots"]:
            got = imgs.get(s["frame"])
            ref_path = refdir / ("%06d.png" % s["frame"])
            if a.cmd == "update":
                if got is None:
                    print("  %s %d: no image" % (c["name"], s["frame"])); ok_all = False
                    continue
                refdir.mkdir(parents=True, exist_ok=True)
                got.save(ref_path)
                continue
            from PIL import Image
            ref = Image.open(ref_path).convert("RGB") if ref_path.exists() else None
            if got is None or ref is None:
                res = {"shot": s["what"], "frame": s["frame"], "passed": False,
                       "why": "no image" if got is None else "no reference"}
            else:
                checks = s.get("checks") or [{"box": None, "max": c.get("max", 0.5)}]
                vals = [(diff(got, ref, k.get("box"), k.get("metric", "changed")), k["max"],
                         k.get("name", "full")) for k in checks]
                res = {"shot": s["what"], "frame": s["frame"],
                       "passed": all(v <= m for v, m, _ in vals),
                       "diffs": {n: round(v, 1) for v, _, n in vals}}
            ok_all &= res["passed"]
            summary.append(dict(case=c["name"], **res))
            rows.append(("%s  %s  %s" % (c["name"], s["what"], "OK" if res["passed"] else "FALLA " + str(res.get("diffs", res.get("why")))),
                         got, ref))
            print("  %-10s %-28s %s %s" % (c["name"], s["what"], "OK   " if res["passed"] else "FALLA",
                                           res.get("diffs", res.get("why"))))
        errs = [l for l in log.splitlines()
                if re.search(r"(?i)\berror\b|omitidos [1-9]|out of range|does not fit", l)
                and not re.search(r"\b0 error\(s\)", l)]
        if errs and a.cmd == "run":
            print("  avisos del log:", errs[:5])
        if rows:
            sheet(rows, OUT / ("%s.png" % c["name"]))
        print("%s: %.0f s" % (c["name"], time.time() - t0))
    if a.cmd == "run":
        (OUT / "resumen.json").write_text(json.dumps({"passed": ok_all, "shots": summary},
                                                     ensure_ascii=False, indent=1), encoding="utf-8")
        print("RESULTADO:", "TODO OK" if ok_all else "HAY FALLAS", "->", OUT)
        sys.exit(0 if ok_all else 1)


if __name__ == "__main__":
    main()
