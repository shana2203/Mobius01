"""List the MSYS2 files the per-PC compile needs: the UCRT64 packages that
Builder.cs requires plus everything they depend on, nothing else (no bash,
pacman, python unless a dependency, docs, man pages or translations).

    python -I toolchain_files.py <msys root> <out list> <out packages>

Writes one path per line (relative to the MSYS2 root, forward slashes) for
the package files and their pacman database entries (var/lib/pacman/local/...),
which Builder.cs reads to know the packages are installed.
"""
import os
import subprocess
import sys

# python: runs tools/ps2recomp and tools/vurecomp, which make the game's code
# from the player's own SLUS_201.52 (the download does not carry it)
# python-pillow: tools/ac4_espanol_mod.py (the Spanish mod made from the disc
# when the game has none) draws the translated cards and labels
NEEDED = ["gcc", "cmake", "ninja", "pkgconf", "sdl3", "shaderc", "vulkan-headers", "vulkan-loader",
          "python", "python-pillow"]
PREFIX = "mingw-w64-ucrt-x86_64-"
# never needed to compile or run the compiler
SKIP_DIRS = ("share/doc/", "share/man/", "share/info/", "share/gtk-doc/", "share/locale/",
             "share/cmake/Help/", "share/bash-completion/", "share/emacs/", "share/vim/", "share/aclocal/")


def pacman(root, *args):
    bash = os.path.join(root, "usr", "bin", "bash.exe")
    cmd = "pacman " + " ".join(args)
    return subprocess.run([bash, "-lc", cmd], capture_output=True, text=True, encoding="utf-8",
                          env=dict(os.environ, MSYSTEM="UCRT64", CHERE_INVOKING="1",
                                   LANG="C", LC_ALL="C")).stdout   # English field names


def strip_version(dep):
    for op in (">=", "<=", "=", ">", "<"):
        if op in dep:
            return dep.split(op, 1)[0]
    return dep.strip()


def main():
    root, out_list, out_pkgs = sys.argv[1], sys.argv[2], sys.argv[3]
    info = pacman(root, "-Qi")
    pkgs, provides = {}, {}
    cur = None
    for line in info.splitlines():
        if ":" not in line:
            continue
        key, _, val = line.partition(":")
        key, val = key.strip(), val.strip()
        if key == "Name":
            cur = {"name": val, "deps": [], "version": ""}
            pkgs[val] = cur
        elif cur is None:
            continue
        elif key == "Version":
            cur["version"] = val
        elif key == "Depends On" and val != "None":
            cur["deps"] = [strip_version(d) for d in val.split()]
        elif key == "Provides" and val != "None":
            for p in val.split():
                provides.setdefault(strip_version(p), cur["name"])

    todo = [PREFIX + n for n in NEEDED]
    keep = set()
    while todo:
        n = todo.pop()
        n = n if n in pkgs else provides.get(n)
        if n is None or n in keep:
            continue
        keep.add(n)
        todo.extend(pkgs[n]["deps"])
    # only native UCRT64 packages go in; the MSYS side (bash, pacman...) is not
    # used by the compile
    msys_side = sorted(k for k in keep if not k.startswith(PREFIX))
    keep = sorted(k for k in keep if k.startswith(PREFIX))
    if len(keep) < len(NEEDED):
        sys.exit("could not read the installed packages (%d found)" % len(keep))

    files = []
    for line in pacman(root, "-Ql", *keep).splitlines():
        _, _, path = line.partition(" ")
        rel = path.lstrip("/")
        if not rel or rel.endswith("/"):
            continue
        sub = rel.split("/", 1)[1] if "/" in rel else rel
        if sub.startswith(SKIP_DIRS) or rel.endswith(".pyc"):
            continue
        # Python's own test suite, IDLE and Tk demos: never used by the recompilers
        parts = sub.split("/")
        if len(parts) > 2 and parts[0] == "lib" and parts[1].startswith("python3") and \
                parts[2] in ("test", "idlelib", "tkinter", "turtledemo", "ensurepip", "lib2to3", "__pycache__"):
            continue
        if os.path.isfile(os.path.join(root, rel)):
            files.append(rel)
    db = os.path.join(root, "var", "lib", "pacman", "local")
    for k in keep:
        d = "%s-%s" % (k, pkgs[k]["version"])
        for f in os.listdir(os.path.join(db, d)):
            files.append("var/lib/pacman/local/%s/%s" % (d, f))
    with open(out_list, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(files) + "\n")
    with open(out_pkgs, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join("%s %s" % (k, pkgs[k]["version"]) for k in keep) + "\n")
    size = sum(os.path.getsize(os.path.join(root, p)) for p in files)
    print("%d packages, %d files, %.1f MB (MSYS side left out: %s)"
          % (len(keep), len(files), size / 1048576.0, ", ".join(msys_side) or "none"))


if __name__ == "__main__":
    main()
