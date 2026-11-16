"""Mod files that are made from the game's own data, generated on the
player's PC from their ISO -- the download never carries them.  Jugar.exe
runs this before the first compile (and again if one of them is missing).

    python ac4_mods_from_disc.py <us.iso> <SLUS_201.52> <mods dir> [on|off]

  espanol/       the Spanish mod: our translation only (ac4_espanol_mod.py);
                 made only with "on" (the player plays in Spanish)
  mision_libre/  files/BIN/DATA.CDP.0272.PARTS, the free-mission hangar menu
                 (ac4_free_mission.py); the mod keeps its own on/off
Each part is made only when missing; one failing does not stop the others.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def main(iso, elf, mods, state="off"):
    failed = 0
    es = os.path.join(mods, "espanol")
    # English: the game is played in its original language, no Spanish mod
    if state == "on" and not os.path.isdir(os.path.join(es, "files", "BIN")):
        try:
            import ac4_espanol_mod
            ac4_espanol_mod.main(iso, elf, es, state)
        except BaseException as e:   # SystemExit included
            print("espanol: not made (%s)" % e)
            failed += 1
    free = os.path.join(mods, "mision_libre")
    parts = os.path.join(free, "files", "BIN", "DATA.CDP.0272.PARTS")
    if os.path.isfile(os.path.join(free, "mod.toml")) and not os.path.isfile(parts):
        try:
            import ac4_free_mission as F
            from ac4_cdp import Cdp
            os.makedirs(os.path.dirname(parts), exist_ok=True)
            cdp = Cdp(iso)
            F.write_patch(parts, cdp.get(272), cdp.get(177))
            print("mision_libre:", parts)
        except BaseException as e:
            print("mision_libre: not made (%s)" % e)
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    sys.exit(main(*sys.argv[1:5]))
