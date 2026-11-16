"""Minimal ISO 9660 reader for the AC4 discs (no dependencies).

    python iso9660.py list  <iso>
    python iso9660.py get   <iso> <path> <out>
"""
import struct, sys

SECTOR = 2048


class Iso:
    def __init__(self, path):
        self.f = open(path, "rb")
        pvd = self.read(16, SECTOR)
        if pvd[1:6] != b"CD001":
            raise ValueError("not an ISO 9660 image")
        self.root = self._record(pvd[156:190])

    def read(self, lba, size, offset=0):
        self.f.seek(lba * SECTOR + offset)
        return self.f.read(size)

    @staticmethod
    def _record(b):
        lba = struct.unpack_from("<I", b, 2)[0]
        size = struct.unpack_from("<I", b, 10)[0]
        flags = b[25]
        nlen = b[32]
        name = b[33:33 + nlen].decode("ascii", "replace")
        return {"lba": lba, "size": size, "dir": bool(flags & 2), "name": name}

    def listdir(self, rec):
        data = self.read(rec["lba"], rec["size"])
        out, i = [], 0
        while i < len(data):
            n = data[i]
            if n == 0:
                i = (i // SECTOR + 1) * SECTOR
                continue
            r = self._record(data[i:i + n])
            if r["name"] not in ("\x00", "\x01"):
                r["name"] = r["name"].split(";")[0]
                out.append(r)
            i += n
        return out

    def walk(self, rec=None, prefix=""):
        rec = rec or self.root
        for r in self.listdir(rec):
            p = prefix + "/" + r["name"]
            if r["dir"]:
                yield from self.walk(r, p)
            else:
                yield p, r

    def find(self, path):
        want = path.strip("/").upper()
        for p, r in self.walk():
            if p.strip("/").upper() == want:
                return r
        raise FileNotFoundError(path)

    def get(self, path):
        r = self.find(path)
        return self.read(r["lba"], r["size"])


if __name__ == "__main__":
    iso = Iso(sys.argv[2])
    if sys.argv[1] == "list":
        for p, r in iso.walk():
            print("%-40s %10d  lba %d" % (p, r["size"], r["lba"]))
    elif sys.argv[1] == "get":
        open(sys.argv[4], "wb").write(iso.get(sys.argv[3]))
