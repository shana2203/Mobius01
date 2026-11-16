"""The Spanish text over the end of the intro movie, made on the player's PC.

Jugar.exe plays the intro hidden once (a new game on throwaway saves, with
SS01 served as INTRO.SMV and no mods, so the picture is the disc's own) and
saves its pictures from 1730 on (PS2_IPU_DUMP).  This picks the first one
with the whole English text on it -- 1740, the one the overlays were laid
out on, first -- and runs ac4_movie_text.py on it:

    python ac4_intro_text.py <frames dir> <mod movies dir>
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def main(frames, out):
    import ac4_movie_text as M
    order = [1740] + list(range(1741, 1762)) + list(range(1739, 1729, -1))
    for n in order:
        frame = os.path.join(frames, "INTRO_%05d.ppm" % n)
        if not os.path.isfile(frame):
            continue
        sys.argv = ["ac4_movie_text.py", frame, out]
        try:
            M.main()
            print("intro text: made from picture", n)
            return 0
        except SystemExit as e:
            print("intro text: picture %d not usable (%s)" % (n, e))
    print("intro text: no usable picture")
    return 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    sys.exit(main(*sys.argv[1:]))
