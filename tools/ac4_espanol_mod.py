"""The Spanish mod made on the player's PC, from their own disc and only our
texts -- the fallback Jugar.exe uses when the installed game has no
mods/espanol (the download normally carries it ready).

    python ac4_espanol_mod.py <us.iso> <SLUS_201.52> <mod dir> [on|off]

  1. ac4_tr.py export: the US strings with their ids, read from the ISO
     (kept in a work folder, deleted at the end: they never leave the PC);
     ac4_hud.py: the HUD labels redrawn in Spanish with the disc's font;
  2. ac4_tr.py build: our translation (localization/es-419-propio/es) put
     into the disc's text tables as overlay patches (files/BIN/*.PARTS),
     which the engine applies when the game reads DATA.CDP from the disc;
  3. ac4_elf_strings.py + ac4_es.py elf: the executable's block-font texts
     (localization/es-419/ejecutable.json) as script.lua;
  4. mod.toml.
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
LOC = os.path.join(HERE, "..", "localization")


def main(iso, elf, mod, state="on"):
    import ac4_tr as TR
    import ac4_es as E
    import ac4_elf_strings as S
    own = os.path.join(LOC, "es-419-propio", "es")
    if not os.path.isdir(own):
        raise SystemExit("missing localization/es-419-propio/es")
    work = os.path.abspath(mod) + ".work"
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(mod, exist_ok=True)
    try:
        shutil.copytree(own, os.path.join(work, "es"))
        TR.export(iso, work)
        # the HUD labels in Spanish, drawn from the disc's own HUD font
        hud = os.path.join(work, "hud_part0.bin")
        try:
            import ac4_hud as H
            sys.argv = ["ac4_hud.py", iso, hud, os.path.join(work, "hud.png")]
            H.main()
        except Exception as e:
            print("espanol: HUD left in English (%s)" % e)
            hud = None
        TR.build(iso, work, mod, hud)
        table = os.path.join(LOC, "es-419", "ejecutable.json")
        if os.path.isfile(table):
            strings = os.path.join(work, "elf_strings.json")
            sys.argv = ["ac4_elf_strings.py", elf, strings]
            S.main()
            E.elf_script(strings, table, os.path.join(mod, "script.lua"))
        with open(os.path.join(mod, "mod.toml"), "w", encoding="utf-8", newline="\n") as f:
            f.write('name = "Español neutro"\nversion = "0.1"\nauthor = "Mobius01"\n'
                    'game = "SLUS-20152"\nenabled = %s\n' % ("true" if state == "on" else "false"))
        print("espanol: mod generado en", mod)
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    main(*sys.argv[1:5])
