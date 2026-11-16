"""Retain the campaign menu layout used by the shared free-mission hangar.

Entry 272 has longer help text than campaign entry 177. Keep the campaign
layout, append the free-mission help, and retain its translated graphics.
"""
import struct

from ac4_es import write_overlay


def repair_resource(blob, campaign):
    count, menu, second = struct.unpack_from("<III", blob)
    if count != 2 or menu != 16:
        raise ValueError("Unexpected free-mission resource")
    nested_count, colors, text, graphics, icons = struct.unpack_from("<5I", blob, menu)
    if nested_count != 4 or colors != 32:
        raise ValueError("Unexpected free-mission menu layout")
    if menu + graphics != 0xD10 or not graphics < icons < second - menu:
        raise ValueError("Unexpected free-mission graphics layout")
    if struct.unpack_from("<I", blob, menu + graphics)[0] != 22:
        raise ValueError("Unexpected free-mission graphics table")
    if struct.unpack_from("<II", campaign) != (1, 16):
        raise ValueError("Unexpected campaign menu resource")

    text_start = menu + text
    text_end = menu + graphics if text < graphics else len(blob)
    help_text = blob[text_start:text_end]
    if len(help_text) < 4:
        raise ValueError("Missing free-mission help text")

    # The shared hangar also retains addresses within the campaign menu data.
    # Keep that complete layout, then append the free-mission help and texture.
    first = bytearray(campaign[16:])
    graphic_data = blob[menu + graphics:second]
    if len(graphic_data) != len(first) - 0x490:
        raise ValueError("Unexpected free-mission graphics size")
    first[0x490:] = graphic_data
    while len(first) % 16:
        first.append(0)
    struct.pack_into("<I", first, 8, len(first))
    first.extend(help_text)
    while len(first) % 16:
        first.append(0)
    header = struct.pack("<4I", 2, 16, 16 + len(first), 0)
    return header + first + blob[second:text_start if text_start >= second else len(blob)]


def write_patch(path, blob, campaign):
    repaired = repair_resource(blob, campaign)
    write_overlay(path, len(repaired), [(0, repaired)])


if __name__ == "__main__":
    import argparse
    from pathlib import Path
    from ac4_cdp import Cdp

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("iso")
    parser.add_argument("mod_dir")
    args = parser.parse_args()
    target = Path(args.mod_dir) / "files" / "BIN" / "DATA.CDP.0272.PARTS"
    target.parent.mkdir(parents=True, exist_ok=True)
    cdp = Cdp(args.iso)
    write_patch(target, cdp.get(272), cdp.get(177))
    print(target)
