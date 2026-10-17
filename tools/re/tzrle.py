"""Prototype decoder for Tzar `.RLE` sprite sheets (see docs/formats/rle.md).

Library use:  sheet = load(path); sheet.rows -> list of rows of runs.
CLI:          python3 -I tzrle.py <file.RLE> <out.png>
"""
import struct
import sys

MAGIC = b' elr'
TYPE_PAL8, TYPE_RGB555, TYPE_SHADOW = 1, 3, 7


class RleError(ValueError):
    pass


class Sheet:
    pass


def load(path):
    with open(path, 'rb') as f:
        return parse(f.read())


def parse(d):
    if len(d) < 0x26 or d[0:4] != MAGIC or d[0x10:0x14] != b'RLE\0':
        raise RleError('bad magic')
    s = Sheet()
    s.dirs, s.frames, s.reserved = struct.unpack_from('<III', d, 4)
    s.width, s.height, s.kind, s.data_size, s.pal_count, s.key = struct.unpack_from('<HHHIII', d, 0x14)
    if s.kind not in (TYPE_PAL8, TYPE_RGB555, TYPE_SHADOW):
        raise RleError('unknown kind %d' % s.kind)
    if s.kind != TYPE_PAL8 and s.pal_count != 0:
        raise RleError('palette in non-indexed sheet')
    s.palette = []  # (r, g, b) tuples, stored on disk as B, G, R, 0
    for i in range(s.pal_count):
        b, g, r, _ = d[0x26 + 4 * i: 0x2a + 4 * i]
        s.palette.append((r, g, b))
    table = 0x26 + 4 * s.pal_count
    if table + s.data_size != len(d):
        raise RleError('size mismatch')
    offs = list(struct.unpack_from('<%dI' % s.height, d, table)) + [s.data_size]
    bpp = {TYPE_PAL8: 1, TYPE_RGB555: 2, TYPE_SHADOW: 0}[s.kind]
    s.rows = []  # per row: list of (x, count, values); values is None for shadow sheets
    for y in range(s.height):
        p, e = table + offs[y], table + offs[y + 1]
        if not (table + 4 * s.height <= p <= e <= len(d)):
            raise RleError('bad row offset at %d' % y)
        x, runs = 0, []
        while p < e:
            x += d[p]
            p += 1
            if p >= e:
                break
            n = d[p]
            p += 1
            if n:
                if bpp == 1:
                    vals = list(d[p:p + n])
                elif bpp == 2:
                    vals = list(struct.unpack_from('<%dH' % n, d, p))
                else:
                    vals = None
                runs.append((x, n, vals))
                p += n * bpp
                x += n
        if x != s.width or p != e:
            raise RleError('row %d does not fill width (%d/%d)' % (y, x, s.width))
        s.rows.append(runs)
    if s.dirs * s.frames == 0 or s.height < s.dirs * s.frames:
        raise RleError('bad dirs/frames')
    # Cells are stacked vertically, direction-major. One original file
    # (ANIMS/BRIBE.RLE) has a single surplus row at the bottom; it is ignored.
    s.cell_h = s.height // (s.dirs * s.frames)
    s.extra_rows = s.height - s.cell_h * s.dirs * s.frames
    return s


def x5(c):
    """Expand a 5-bit channel to 8 bits by bit replication (TDD 13.2)."""
    return (c << 3) | (c >> 2)


def rgb555(v):
    return (x5((v >> 10) & 31), x5((v >> 5) & 31), x5(v & 31))


def is_shadow_rgb(rgb):
    """Heuristic baked-shadow test (docs/formats/rle.md, 'Baked unit shadows').

    True when the colour quantised to RGB555 has R5 == 0, B5 == 0 and G5 <= 2.
    """
    r, g, b = rgb
    return (r >> 3) == 0 and (b >> 3) == 0 and (g >> 3) <= 2


PLAYER_FIRST = 240  # indexed sheets: palette indices 240..255 are the player-colour ramp


def load_game_pal(path):
    """GAME.PAL: 'PALF', uint16 count, uint16 0, uint32 0, then count x (B, G, R, 0)."""
    with open(path, 'rb') as f:
        d = f.read()
    if d[:4] != b'PALF':
        raise ValueError('not a PALF file')
    n = struct.unpack_from('<H', d, 4)[0]
    return [(d[12 + 4 * i + 2], d[12 + 4 * i + 1], d[12 + 4 * i]) for i in range(n)]


def player_ramp(game_pal, player):
    """16 colours for player 0..7 (blue, red, green, purple, yellow, cyan, orange, grey)."""
    base = 112 + 16 * player
    return game_pal[base:base + 16]


def to_rgba(s, ramp=None, mark_shadow=False):
    """Decode the whole sheet to RGBA8 bytes (width x height).

    ramp: optional 16-colour player ramp replacing palette indices 240..255.
    mark_shadow: draw baked-shadow pixels as black at alpha 128 instead of their colour.
    Separate shadow sheets (kind 7) always decode to black at alpha 128.
    """
    w = s.width
    out = bytearray(w * s.height * 4)
    if s.kind == TYPE_PAL8:
        lut = []
        for i, c in enumerate(s.palette):
            if ramp is not None and i >= PLAYER_FIRST:
                c = ramp[i - PLAYER_FIRST]
            px = bytes((c[0], c[1], c[2], 255))
            if mark_shadow and is_shadow_rgb(c) and i < PLAYER_FIRST:
                px = b'\0\0\0\x80'
            lut.append(px)
    for y, runs in enumerate(s.rows):
        base = y * w * 4
        for x, n, vals in runs:
            o = base + x * 4
            if s.kind == TYPE_SHADOW:
                out[o:o + 4 * n] = b'\0\0\0\x80' * n
            elif s.kind == TYPE_PAL8:
                out[o:o + 4 * n] = b''.join(lut[v] for v in vals)
            else:
                chunk = bytearray()
                for v in vals:
                    c = rgb555(v)
                    if mark_shadow and v == 0x0020:
                        chunk += b'\0\0\0\x80'
                    else:
                        chunk += bytes((c[0], c[1], c[2], 255))
                out[o:o + 4 * n] = chunk
    return bytes(out)


def save_png(s, path, ramp=None, mark_shadow=False):
    from PIL import Image
    Image.frombytes('RGBA', (s.width, s.height), to_rgba(s, ramp, mark_shadow)).save(path)


if __name__ == '__main__':
    save_png(load(sys.argv[1]), sys.argv[2])
