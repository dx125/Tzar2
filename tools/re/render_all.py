"""Decode every .RLE under an install, write PNG contact sheets and sprite-volume stats.

Usage: python3 -I render_all.py <install root> <out dir> [--no-png]

PNG layout: one row per direction, one column per frame (cells of width x cell_h).
Indexed sheets are tinted with player 0 (blue) and baked shadows are drawn as
50 % black, so the result looks like the in-game sprite over a neutral background.
Writes <out dir>/volume.json with the totals used by spike M0-S6.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tzrle  # noqa: E402


def frame_boxes(s):
    """Yield (cell_index, bbox or None) with inclusive bbox in cell coordinates."""
    for i in range(s.dirs * s.frames):
        x0 = y0 = 10 ** 9
        x1 = y1 = -1
        for y in range(i * s.cell_h, (i + 1) * s.cell_h):
            runs = s.rows[y]
            if runs:
                x0 = min(x0, runs[0][0])
                x1 = max(x1, runs[-1][0] + runs[-1][1] - 1)
                y0 = min(y0, y - i * s.cell_h)
                y1 = y - i * s.cell_h
        yield i, ((x0, y0, x1, y1) if x1 >= 0 else None)


def main(root, out, write_png=True):
    pal = tzrle.load_game_pal(os.path.join(root, 'IMAGES', 'GAME.PAL'))
    ramp = tzrle.player_ramp(pal, 0)
    files = []
    for dp, _, fns in os.walk(os.path.join(root, 'IMAGES')):
        files += [os.path.join(dp, f) for f in fns if f.upper().endswith('.RLE')]
    files.sort()
    tot = dict(files=0, errors=0, cells=0, empty_cells=0, sheet_px=0, cell_px=0, bbox_px=0,
               opaque_px=0, rle_bytes=0, by_kind={}, by_folder={})
    errors = []
    t0 = time.time()
    os.makedirs(out, exist_ok=True)
    if write_png:
        from PIL import Image
    for path in files:
        rel = os.path.relpath(path, root)
        try:
            s = tzrle.load(path)
        except tzrle.RleError as e:
            tot['errors'] += 1
            errors.append((rel, str(e)))
            continue
        tot['files'] += 1
        size = os.path.getsize(path)
        cells = s.dirs * s.frames
        opaque = sum(n for runs in s.rows for _, n, _ in runs)
        bbox = 0
        for _, b in frame_boxes(s):
            if b is None:
                tot['empty_cells'] += 1
            else:
                bbox += (b[2] - b[0] + 1) * (b[3] - b[1] + 1)
        cell_px = s.width * s.cell_h * cells
        for key, bucket in (('by_kind', str(s.kind)), ('by_folder', os.path.dirname(rel).split(os.sep)[1])):
            d = tot[key].setdefault(bucket, dict(files=0, cell_px=0, bbox_px=0, opaque_px=0, rle_bytes=0))
            d['files'] += 1
            d['cell_px'] += cell_px
            d['bbox_px'] += bbox
            d['opaque_px'] += opaque
            d['rle_bytes'] += size
        tot['cells'] += cells
        tot['sheet_px'] += s.width * s.height
        tot['cell_px'] += cell_px
        tot['bbox_px'] += bbox
        tot['opaque_px'] += opaque
        tot['rle_bytes'] += size
        if write_png:
            sheet = Image.frombytes('RGBA', (s.width, s.height), tzrle.to_rgba(s, ramp, mark_shadow=True))
            grid = Image.new('RGBA', (s.width * s.frames, s.cell_h * s.dirs), (96, 112, 96, 255))
            for i in range(cells):
                cell = sheet.crop((0, i * s.cell_h, s.width, (i + 1) * s.cell_h))
                grid.alpha_composite(cell, ((i % s.frames) * s.width, (i // s.frames) * s.cell_h))
            name = rel.replace(os.sep, '__')[:-4] + '.png'
            grid.save(os.path.join(out, name))
    tot['seconds'] = round(time.time() - t0, 1)
    tot['error_list'] = errors
    with open(os.path.join(out, 'volume.json'), 'w') as f:
        json.dump(tot, f, indent=1)
    print(json.dumps({k: v for k, v in tot.items() if k not in ('by_folder',)}, indent=1))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], '--no-png' not in sys.argv)
