"""Prototype decoder for Tzar `.FNT` bitmap fonts (see docs/formats/fnt.md).

CLI: python3 -I tzfnt.py <font.FNT> <out.png>   (renders a specimen sheet)
"""
import struct
import sys


class Font:
    pass


def parse(d):
    f = Font()
    f.face = d[:32].split(b'\0')[0].decode('latin-1')
    (f.height, f.f22, f.f24, f.f26, f.f28, f.f2a, f.f2c, f.f2e,
     f.glyph_count) = struct.unpack_from('<9H', d, 32)
    f.widths = list(d[0x32:0x132])
    f.charmap = list(d[0x132:0x232])
    if d[0x232:0x234] != b'BM':
        raise ValueError('embedded BMP missing')
    bmp = 0x232
    off_bits = struct.unpack_from('<I', d, bmp + 10)[0]
    _, w, h, _, bpp, comp = struct.unpack_from('<IiiHHI', d, bmp + 14)
    if bpp != 8 or comp != 0:
        raise ValueError('unexpected glyph bitmap format')
    stride = (w + 3) & ~3
    f.cell_w, f.strip_h = w, abs(h)
    if f.strip_h % f.height:
        raise ValueError('strip height is not a multiple of the cell height')
    # The header count is not always exact (FIXSHAD.FNT says 94, holds 95);
    # the strip height is authoritative.
    f.glyphs_in_strip = f.strip_h // f.height
    if max(f.charmap) >= f.glyphs_in_strip:
        raise ValueError('charmap points past the glyph strip')
    pix = d[bmp + off_bits: bmp + off_bits + stride * f.strip_h]
    if len(pix) != stride * f.strip_h:
        raise ValueError('truncated glyph bitmap')
    rows = [pix[r * stride: r * stride + w] for r in range(f.strip_h)]
    if h > 0:
        rows.reverse()  # bottom-up DIB
    f.rows = rows
    return f


def glyph(f, ch):
    """Return (width, rows) for character code ch (0..255); rows are bytes of coverage values."""
    w = f.widths[ch]
    g = f.charmap[ch]
    top = g * f.height
    return w, [r[:w] for r in f.rows[top: top + f.height]]


def load(path):
    with open(path, 'rb') as fh:
        return parse(fh.read())


if __name__ == '__main__':
    from PIL import Image
    fnt = load(sys.argv[1])
    maxv = max(max(r) for r in fnt.rows) or 1
    cols = 16
    im = Image.new('L', (cols * (fnt.cell_w + 2), 14 * (fnt.height + 2)), 40)
    for ch in range(32, 256):
        w, rows = glyph(fnt, ch)
        ox = ((ch - 32) % cols) * (fnt.cell_w + 2)
        oy = ((ch - 32) // cols) * (fnt.height + 2)
        for y, r in enumerate(rows):
            for x, v in enumerate(r):
                if v:
                    im.putpixel((ox + x, oy + y), 255 if maxv == 1 else min(255, 40 + v * 215 // maxv))
    im = im.resize((im.width * 3, im.height * 3), Image.NEAREST)
    im.save(sys.argv[2])
    print(fnt.face, 'h', fnt.height, 'glyphs', fnt.glyphs_in_strip, 'cell_w', fnt.cell_w, 'max value', maxv)
