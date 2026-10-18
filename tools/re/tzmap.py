"""Prototype dump of legacy maps (.wmp) and campaigns (.wcm); see docs/formats/wmp.md.

Usage: python3 -I tzmap.py <file> [--triggers] [--objects]
Prints the header, kingdoms, object counts per class and (optionally) every trigger record with decoded fields.
Reads only; writes nothing. The C# reader (Tzar2.Formats.Maps) is the implementation, this is the RE prototype.
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hpfs  # noqa: E402

GROUP_START = [0, 0, 1, 3, 39, 44, 74]
ORDERS = ['default', 'move', 'attack', 'capture', 'load', 'unload', 'pickitem', 'patrol', 'moveadvance', 'guard',
          'standground', 'attackground', 'stop', 'dropitems']
# payload layouts per type code (docs/formats/wmp.md §6.4): s string, b u8, w u16, d u32, O objects, A area, H holder
LAYOUT = {0: 'ss', 1: 's', 2: 'bbd', 3: 'Ob', 4: 'Os', 5: 's', 6: 'sb', 7: 's', 8: 'ws', 9: 'ws', 10: '', 11: 's',
          12: 'sA', 13: 'sw', 14: 'sssssb', 15: 'sss', 16: 'wsbAb', 17: '', 18: 'OAb', 19: 'AA', 20: 's', 21: 'O',
          22: 'Asb', 23: 'O', 24: 'bbb', 25: 'Ob', 26: 'O', 27: 's', 1000: 'O', 1001: 'O', 1002: 'OH', 1003: 'OH',
          1004: 'OA', 1005: 'w', 1006: 'w', 1007: 'Os'}


def members(data):
    """(path, bytes) of every file of an HPFS image, nested images included as bytes."""
    h = hpfs.Hpfs(data)
    out = []

    def walk(ino, prefix):
        d = h.read_inode(ino)
        o = 0
        while o < len(d):
            child, kind, n = struct.unpack_from('<IHH', d, o)
            name = d[o + 8:o + 8 + n].decode('cp1252')
            o += 8 + n
            if kind == 1:
                walk(child, prefix + name + '/')
            else:
                out.append((prefix + name, h.read_inode(child)))
    walk(1, '')
    return out


def cstr(b, o, n=None):
    end = b.index(b'\0', o) if n is None else min([o + n] + [i for i in range(o, o + n) if b[i] == 0][:1])
    return b[o:end].decode('cp1252', 'replace'), end + 1


class Cursor:
    def __init__(self, b):
        self.b, self.o = b, 0

    def take(self, fmt):
        v = struct.unpack_from('<' + fmt, self.b, self.o)
        self.o += struct.calcsize('<' + fmt)
        return v[0] if len(v) == 1 else v

    def s(self):
        v, self.o = cstr(self.b, self.o)
        return v

    def field(self, f):
        if f == 's':
            return self.s()
        if f in 'bwd':
            return self.take({'b': 'B', 'w': 'H', 'd': 'I'}[f])
        if f == 'O':
            typed, at_most, cls, players, all_, race, n = self.take('BBHHBBH')
            ids = [self.take('H') for _ in range(n)]
            return {'typed': typed, 'cmp': '<=' if at_most else '>=', 'class': cls, 'players': players, 'all': all_,
                    'race': race, 'ids': ids, 'count': self.s()}
        if f == 'A':
            kind, l, t, r, b, obj = self.take('BIIIIH')
            return {'object': obj, 'distance': self.s()} if kind else {'rect': (l, t, r, b), 'unused': self.s()}
        kind, cls, obj, players = self.take('BHHH')
        return {'typed': kind, 'class': cls, 'object': obj, 'players': players}


def records(b):
    o = 0
    while o < len(b):
        name, _ = cstr(b, o, 32)
        t, n = struct.unpack_from('<HH', b, o + 32)
        yield name, t, b[o + 36:o + 36 + n]
        o += 36 + n


def dump_map(data, show_triggers, show_objects, indent=''):
    files = dict(members(data))
    header_path = next(p for p in files if p.upper().startswith('MAPS/') and p.lower().endswith('.header'))
    name = header_path[5:-7]
    part = lambda p: next((v for k, v in files.items() if k.lower() == ('maps/' + name + '.' + p).lower()), b'')
    h = part('header')
    side = struct.unpack_from('<H', h, 6)[0]
    print('%smap %s: "%s" by "%s", %d x %d, set %s, players %d, pop limit %d' % (
        indent, name, cstr(h, 0x56, 64)[0], cstr(h, 0x96, 64)[0], side, side, cstr(h, 0xD6, 64)[0], h[0x4D],
        struct.unpack_from('<H', h, 0x128)[0]))
    k = part('kingdominfo')
    for i in range(8):
        r = k[i * 152:(i + 1) * 152]
        if r[0x40]:
            print('%s  kingdom %d: %s / %s, civ %d, start %s, ai "%s"' % (
                indent, i + 1, cstr(r, 0, 32)[0], cstr(r, 32, 32)[0], r[0x58], struct.unpack_from('<HH', r, 0x54),
                cstr(r, 0x59, 32)[0]))
    classes = collections.Counter()
    b, o = part('objects'), 0
    while o < len(b):
        kind, enc, size = struct.unpack_from('<BII', b, o)
        g = enc >> 16
        classes[GROUP_START[g] + (enc & 0xFFFF) if 0 < g < 7 else -1] += 1
        o += 9 + size
    trees = sum(1 for (w,) in struct.iter_unpack('<H', part('tiles')) if w >> 11)
    print('%s  objects %d (class index: count %s), tree tiles %d' % (indent, sum(classes.values()),
                                                                     dict(sorted(classes.items())) if show_objects else len(classes), trees))
    for p in ('events', 'actions'):
        recs = list(records(part(p)))
        print('%s  %s: %d' % (indent, p, len(recs)))
        if show_triggers:
            for rn, t, payload in recs:
                c = Cursor(payload)
                fields = [c.field(f) for f in LAYOUT.get(t, '')] if t in LAYOUT else ['?raw %d bytes' % len(payload)]
                print('%s    %-32s %4d %s%s' % (indent, rn, t, fields, '' if c.o == len(payload) or t not in LAYOUT else ' !LENGTH'))
    rules = list(records(part('rules')))
    print('%s  rules: %d' % (indent, len(rules)))
    if show_triggers:
        for rn, n, payload in rules:
            parts = payload.split(b'\0')[:-1]
            print('%s    %-32s if %r then %s' % (indent, rn, parts[0].decode('cp1252'), [x.decode('cp1252') for x in parts[1:]]))


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    data = open(sys.argv[1], 'rb').read()
    triggers, objects = '--triggers' in sys.argv, '--objects' in sys.argv
    nested = [(p, d) for p, d in members(data) if d[:4] == b'HPFS']
    if nested:
        for p, d in nested:
            dump_map(d, triggers, objects, '  ')
    else:
        dump_map(data, triggers, objects)
    return 0


if __name__ == '__main__':
    sys.exit(main())
