"""Merge ac4_pipelines.keys files into one seed list.

The renderer remembers every pipeline state a session used in
ac4_pipelines.keys (format: "AC5PIPE1", u32 count, u32 0, then count u64
keys).  A seed list is the union of those from sessions that covered
different parts of the game; `ac4.exe --precompile` builds all of them
before play, and the runtime loads <prefix>.seed.keys next to its own list.

    python tools/merge_pipeline_keys.py -o config/ac4/ac4_pipelines.seed.keys \
        a.keys b.keys ...
"""
import argparse
import struct
import sys

MAGIC = b"AC5PIPE1"


def read_keys(path):
    data = open(path, "rb").read()
    if len(data) < 16 or data[:8] != MAGIC:
        raise SystemExit("%s: not a pipeline keys file" % path)
    (count,) = struct.unpack_from("<I", data, 8)
    if 16 + count * 8 > len(data):
        raise SystemExit("%s: truncated" % path)
    return list(struct.unpack_from("<%dQ" % count, data, 16))


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("-o", "--output", required=True)
    ap.add_argument("inputs", nargs="+")
    args = ap.parse_args(argv)
    seen, keys = set(), []
    for path in args.inputs:
        try:
            got = read_keys(path)
        except FileNotFoundError:
            print("skipping missing %s" % path)
            continue
        new = [k for k in got if k not in seen and not k >> 48]
        seen.update(new)
        keys.extend(new)
        print("%-60s %4d states, %4d new" % (path, len(got), len(new)))
    with open(args.output, "wb") as f:
        f.write(MAGIC + struct.pack("<II", len(keys), 0))
        f.write(struct.pack("<%dQ" % len(keys), *keys))
    print("wrote %s: %d states" % (args.output, len(keys)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
