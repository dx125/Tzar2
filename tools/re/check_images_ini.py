"""Cross-check DATA/IMAGES.INI (and terrain *.IMAGES.INI) against decoded RLE sheets.

Usage: python3 -I check_images_ini.py <install root>
"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tzini  # noqa: E402
import tzrle  # noqa: E402


def index_files(root):
    idx = {}
    for dp, _, fns in os.walk(root):
        for fn in fns:
            idx[os.path.relpath(os.path.join(dp, fn), root).replace(os.sep, '/').lower()] = os.path.join(dp, fn)
    return idx


def resolve(idx, base, logical):
    stem = (base + '/' + logical.replace('\\', '/')).lower()
    stem = stem[:-4] if stem.endswith('.bmp') else stem
    for ext in ('.rle', '.bmp'):
        if stem + ext in idx:
            return idx[stem + ext]
    return None


def main(root):
    idx = index_files(root)
    stats = Counter()
    problems = []
    inis = [('data/images.ini', 'images')]
    for p in sorted(idx):
        if p.startswith('images/terrain/') and p.endswith('.images.ini'):
            inis.append((p, 'images/terrain/' + p.split('/')[2].split('.')[0] if p.count('/') == 2 else os.path.dirname(p)))
    for ini, base in inis:
        secs, warns = tzini.load(idx[ini])
        stats['ini_warnings'] += len(warns)
        for name, entries in secs:
            d = {k.lower(): v for k, v in entries if k}
            if 'image' not in d:
                continue
            stats['entries'] += 1
            path = resolve(idx, base, d['image'])
            if path is None and base != 'images':
                path = resolve(idx, 'images', d['image'])
            if path is None:
                stats['missing'] += 1
                problems.append((ini, name, 'missing', d['image']))
                continue
            if not path.upper().endswith('.RLE'):
                stats['bmp'] += 1
                continue
            stats['rle'] += 1
            s = tzrle.load(path)
            rows = int(d.get('rows', '1'))
            cols = int(d.get('cols', '1'))
            if rows == s.frames and cols == s.dirs:
                stats['rows_cols_match'] += 1
            else:
                problems.append((ini, name, 'rows/cols %d/%d vs frames/dirs %d/%d' % (rows, cols, s.frames, s.dirs), d['image']))
            if 'delay' in d and 'anim_time' in d:
                delays = [int(x) for x in d['delay'].split(',') if x.strip()]
                stats['delay_count_ok' if len(delays) == rows else 'delay_count_bad'] += 1
                stats['delay_sum_ok' if sum(delays) == int(d['anim_time']) else 'delay_sum_bad'] += 1
    for k in sorted(stats):
        print('%-18s %d' % (k, stats[k]))
    for p in problems:
        print('  ', *p)


if __name__ == '__main__':
    main(sys.argv[1])
