"""Prototype reader for Tzar sprite `.DAT` files (see docs/formats/dat.md).

Usage: python3 -I tzdat.py <install root>   -- parses every DAT and checks frame boxes
"""
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tzrle  # noqa: E402


class Dat:
    pass


def parse(d):
    t = Dat()
    t.rect = struct.unpack_from('<4h', d, 0)
    if t.rect == (0, 0, 0, 0):
        t.grid_w = t.grid_h = 0
    else:
        t.grid_w = (t.rect[2] - t.rect[0] + 1) // 32
        t.grid_h = (t.rect[3] - t.rect[1] + 1) // 32
    p = 8 + t.grid_w * t.grid_h
    if p > len(d):
        raise ValueError('truncated grid')
    t.grid = [list(d[8 + r * t.grid_w: 8 + (r + 1) * t.grid_w]) for r in range(t.grid_h)]
    t.boxes = None
    if p < len(d):
        n = struct.unpack_from('<H', d, p)[0]
        if len(d) != p + 2 + 8 * n:
            raise ValueError('bad box list length')
        t.boxes = [struct.unpack_from('<4h', d, p + 2 + 8 * i) for i in range(n)]
    return t


def cell_bbox(s, cell):
    x0 = y0 = None
    x1 = y1 = -1
    for y in range(cell * s.cell_h, (cell + 1) * s.cell_h):
        runs = s.rows[y]
        if runs:
            yy = y - cell * s.cell_h
            x0 = runs[0][0] if x0 is None else min(x0, runs[0][0])
            x1 = max(x1, runs[-1][0] + runs[-1][1] - 1)
            y0 = yy if y0 is None else y0
            y1 = yy
    return None if x0 is None else (x0, y0, x1, y1)


def main(root):
    stats = Counter()
    for dp, _, fns in os.walk(os.path.join(root, 'IMAGES')):
        for fn in sorted(fns):
            if not fn.upper().endswith('.DAT'):
                continue
            path = os.path.join(dp, fn)
            with open(path, 'rb') as f:
                t = parse(f.read())
            stats['parsed'] += 1
            stats['grid' if t.grid_w else 'no_grid'] += 1
            rle = path[:-4] + '.RLE'
            if t.boxes is None or not os.path.exists(rle):
                continue
            s = tzrle.load(rle)
            if len(t.boxes) != s.dirs * s.frames:
                stats['box_count_mismatch'] += 1
                continue
            for frame in range(s.frames):
                for d in range(s.dirs):
                    ok = cell_bbox(s, d * s.frames + frame) == tuple(t.boxes[frame * s.dirs + d])
                    stats['box_ok' if ok else 'box_diff'] += 1
    for k in sorted(stats):
        print('%-20s %d' % (k, stats[k]))


if __name__ == '__main__':
    main(sys.argv[1])
