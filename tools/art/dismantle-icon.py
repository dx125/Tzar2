"""Draws Tzar2's own order button for taking a building down (BLD-027): a roof split in two, its halves falling apart,
over a hammer, embossed in gold on a transparent ground like the original's order buttons. Tzar2's own art, made by this script.

Usage: python tools/art/dismantle-icon.py [out.png]   (default: src/Tzar2.Client/icons/dismantle.png; needs Pillow)
"""
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 48
SCALE = 4  # drawn at 4x, then reduced, for smooth edges
S = SIZE * SCALE


def poly(draw, points):
    draw.polygon([(x * SCALE, y * SCALE) for x, y in points], fill=255)


def rotated(points, cx, cy, degrees):
    a = math.radians(degrees)
    c, s = math.cos(a), math.sin(a)
    return [(cx + (x - cx) * c - (y - cy) * s, cy + (x - cx) * s + (y - cy) * c) for x, y in points]


def line(draw, a, b, width):
    draw.line([(a[0] * SCALE, a[1] * SCALE), (b[0] * SCALE, b[1] * SCALE)], fill=255, width=round(width * SCALE))


def bar(cx, cy, length, thickness, degrees):
    """A rectangle centred at (cx, cy), its length along the angle."""
    a = math.radians(degrees)
    ux, uy = math.cos(a), math.sin(a)
    px, py = -uy, ux
    h, t = length / 2, thickness / 2
    return [(cx + ux * h + px * t, cy + uy * h + py * t), (cx - ux * h + px * t, cy - uy * h + py * t),
            (cx - ux * h - px * t, cy - uy * h - py * t), (cx + ux * h - px * t, cy + uy * h - py * t)]


def shape():
    m = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(m)
    # The roof of the build buttons, a band and a thin line under it, split at the ridge, its halves sagging apart.
    line(d, (2.5, 20), (19, 10), 4.2)
    line(d, (29, 10), (45.5, 20), 4.2)
    line(d, (7, 22.5), (18.5, 15.5), 2.0)
    line(d, (29.5, 15.5), (41, 22.5), 2.0)
    # A board falling out of the gap.
    poly(d, bar(24, 13.5, 7, 3.0, 75))
    # A hammer below, its head across the top of the handle, striking up at the gap.
    line(d, (13, 44.5), (26, 31.5), 4.0)
    poly(d, bar(27, 30.5, 14, 5.4, 45))
    poly(d, bar(22.6, 26.1, 3, 6.4, 45))  # the striking face, toward the ridge
    return m


def emboss(mask):
    """Gold with light from the upper left: a bright rim above and left, a dark rim below and right."""
    e = 2 * SCALE // 2
    up = ImageChops.offset(mask, e, e)
    down = ImageChops.offset(mask, -e, -e)
    light = ImageChops.subtract(mask, up)    # inside, but not inside when shifted down-right: upper-left edges
    shade = ImageChops.subtract(mask, down)  # lower-right edges
    base = (196, 140, 52)
    hi = (255, 222, 140)
    lo = (96, 58, 14)
    out = Image.new('RGB', (S, S), (0, 0, 0))
    gold = Image.new('RGB', (S, S), base)
    # A soft gradient: lighter above.
    grad = Image.linear_gradient('L').resize((S, S))
    top = Image.new('RGB', (S, S), (232, 176, 80))
    gold = Image.composite(gold, top, grad)
    out.paste(gold, mask=mask)
    out.paste(Image.new('RGB', (S, S), hi), mask=light.filter(ImageFilter.GaussianBlur(SCALE / 2)))
    out.paste(Image.new('RGB', (S, S), lo), mask=ImageChops.multiply(shade.filter(ImageFilter.GaussianBlur(SCALE / 2)), mask))
    return out


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'Tzar2.Client', 'icons', 'dismantle.png')
    mask = shape()
    colour = emboss(mask).resize((SIZE, SIZE), Image.LANCZOS)
    # As the original's buttons (1-bit alpha): the pictogram opaque on a transparent ground, so the card's stone shows
    # round it, with a dark rim a pixel wide below and right of it (the seventh play-test: a black square stood out).
    body = mask.resize((SIZE, SIZE), Image.LANCZOS).point(lambda v: 255 if v >= 110 else 0)
    rim = ImageChops.subtract(ImageChops.lighter(body, ImageChops.offset(body, 1, 1)), body)
    icon = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(Image.new('RGBA', (SIZE, SIZE), (24, 16, 6, 255)), mask=rim)
    icon.paste(colour.convert('RGBA'), mask=body)
    icon.save(target, optimize=True)
    print('wrote', os.path.normpath(target))


if __name__ == '__main__':
    main()
