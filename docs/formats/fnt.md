# `.FNT` bitmap fonts

Status: **resolved** (7/7 files decode; specimen sheets visually correct). Resolves **UNK-06**.
Prototype: [`tools/re/tzfnt.py`](../../tools/re/tzfnt.py).

Files: `LOCAL/FONTS/{ARIAL10_AA, ARIAL12B_AA, ARIAL14B_AA, AV10, CGB_22B, FIXSHAD, SMALL}.FNT`.

## Layout

| Offset | Size | Field |
|---|---|---|
| 0x000 | 32 | face name, NUL-padded ASCII (`Small Fonts`, `Arial`, `AveFifteen`, `Copperplate Gothic Bold`, `cen14b`) |
| 0x020 | uint16 | `cellHeight` — height of every glyph cell in pixels |
| 0x022 | uint16 × 7 | metrics, ⚠ partly understood: 0x022 ≈ ascent (cellHeight − 1 or similar), 0x02A ≈ baseline/ascent of capitals, others small values (0–8); not needed for rendering |
| 0x030 | uint16 | `glyphCount` as written by the tool (may be off by one: `FIXSHAD` says 94, holds 95) |
| 0x032 | uint8 × 256 | advance width per character code 0–255 (0 for codes without a glyph) |
| 0x132 | uint8 × 256 | character code → glyph index in the strip |
| 0x232 | BMP file | a complete embedded Windows BMP (`BM` header, `BITMAPINFOHEADER`), 8 bpp, uncompressed, bottom-up |

The width table starts at 0x032, directly after the `glyphCount` word; widths for codes
0–31 are always 0 and their charmap entries are 0.

## Glyph strip

The embedded bitmap is `cellWidth` wide (= maximum glyph width) and
`glyphs × cellHeight` high. Glyph `g` occupies rows `g × cellHeight … (g+1) × cellHeight − 1`
counted **from the top** (remember to flip the bottom-up DIB). Character `c` is drawn from
the left `widths[c]` columns of glyph `charmap[c]`. The authoritative glyph count is
`stripHeight / cellHeight`.

Pixel values (the embedded palette, if present, is irrelevant except for FIXSHAD):

| Font | Values | Meaning |
|---|---|---|
| `SMALL`, `AV10` | 0, 1 | 1-bit coverage |
| `ARIAL*_AA`, `CGB_22B` | 0–7 | 3-bit anti-aliasing coverage (alpha = v / 7) |
| `FIXSHAD` | 0, 250, 255 | 255 = glyph (white), 0 = drop shadow (black), 250 = background (palette entry is pure green, i.e. transparent) |

Embedded BMP header quirks: AA fonts declare `biClrUsed = 0` but store **no palette**
(pixel data starts at `bfOffBits`, which must be used instead of computing the offset).

## Code page

The English build's fonts contain the Windows-1252 repertoire: 224 glyphs for codes
32–255 (`€` at 0x80, accented Latin letters at 0xC0–0xFF). A localized edition ships fonts
whose glyphs follow its code page (e.g. Cyrillic for CP1251); the `charmap` makes the
mapping explicit. Convert text to the font's code page before lookup, or build a Unicode
→ glyph map at import time from (code page, `charmap`).

| File | Face | cellHeight | cellWidth | glyphs |
|---|---|---|---|---|
| ARIAL10_AA | Arial | 13 | 12 | 224 |
| ARIAL12B_AA | Arial (bold) | 14 | 12 | 224 |
| ARIAL14B_AA | Arial (bold) | 17 | 15 | 224 |
| AV10 | AveFifteen | 16 | 15 | 224 |
| CGB_22B | Copperplate Gothic Bold | 22 | 26 | 223 |
| FIXSHAD | cen14b | 17 | 14 | 95 (ASCII 32–126) |
| SMALL | Small Fonts | 11 | 10 | 224 |
