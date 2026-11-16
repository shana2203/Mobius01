"""BIN/DATA.CDP of Ace Combat 04: an archive indexed by BIN/DATA.TBL, a list of
(start sector, size in bytes) pairs.  Reads straight from the ISO.

    python ac4_cdp.py list  <iso>                 entries, size, magic
    python ac4_cdp.py get   <iso> <index> <out>
    python ac4_cdp.py grep  <iso> <text> [enc]    entries containing text
"""
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from iso9660 import Iso, SECTOR


class Cdp:
    def __init__(self, iso_path):
        self.iso = Iso(iso_path)
        tbl = self.iso.get("/BIN/DATA.TBL")
        self.entries = [struct.unpack_from("<II", tbl, i) for i in range(0, len(tbl) - 7, 8)]
        self.base = self.iso.find("/BIN/DATA.CDP")["lba"]

    def get(self, i):
        sec, size = self.entries[i]
        return self.iso.read(self.base + sec, size)


if __name__ == "__main__":
    c = Cdp(sys.argv[2])
    if sys.argv[1] == "list":
        for i, (sec, size) in enumerate(c.entries):
            head = c.iso.read(c.base + sec, 16) if size else b""
            print("%4d  sec %8d  size %10d  %s  %s" % (i, sec, size, head[:16].hex(),
                  "".join(chr(b) if 32 <= b < 127 else "." for b in head[:16])))
    elif sys.argv[1] == "get":
        open(sys.argv[4], "wb").write(c.get(int(sys.argv[3])))
    elif sys.argv[1] == "grep":
        enc = sys.argv[4] if len(sys.argv) > 4 else "latin-1"
        needle = sys.argv[3].encode(enc)
        for i, (sec, size) in enumerate(c.entries):
            if size and size < 64 << 20:
                d = c.get(i)
                n = d.count(needle)
                if n:
                    print("%4d  size %10d  hits %d  first @%d" % (i, size, n, d.find(needle)))
