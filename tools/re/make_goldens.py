"""Write golden RGBA renderings of 20 representative RLE sheets with the proven prototype decoder.

Usage: python3 -I make_goldens.py <install root> <out dir>

The C# importer is compared against these pixel by pixel (Tzar2.Import.Tests, GoldenSpriteTests; M1 exit
criterion "20 golden sprite comparisons pixel-identical"). Outputs are derived from copyrighted art: write them
only to a git-ignored folder such as tests/local-goldens/sprites (the default the tests look in).

Per sheet: <name>.rgba = raw RGBA8 of the whole sheet (width x height, as tzrle.to_rgba returns it), rendered with
player ramp `player` (indexed sheets) and, for unit sheets, baked shadows marked (mark_shadow=True).
goldens.json lists the sheets with their parameters.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tzrle  # noqa: E402

# Edge cases named in docs/formats/rle.md, then a deterministic spread over the sorted file list.
NAMED = [
    'IMAGES/ANIMS/BRIBE.RLE',                    # surplus row
    'IMAGES/UNIT/ASIAN HORSARCHER IDLE2.RLE',    # 120-entry palette
    'IMAGES/UNIT/EAGLE.RLE',                     # 24 directions
    'IMAGES/UNIT/COW.RLE',                       # RGB555 unit with baked shadow 0x0020
    'IMAGES/UNIT/EURO KNIGHT.RLE',               # indexed unit, player colour
    'IMAGES/UNIT/EAGLE_S.RLE',                   # shadow sheet
]
COUNT = 20


def main(root, out):
    files = []
    for dp, _, fns in os.walk(os.path.join(root, 'IMAGES')):
        files += [os.path.relpath(os.path.join(dp, f), root).replace(os.sep, '/') for f in fns if f.upper().endswith('.RLE')]
    files.sort()
    upper = {f.upper(): f for f in files}
    chosen = [upper[n] for n in NAMED if n in upper]
    stride = max(1, len(files) // (COUNT - len(chosen) + 1))
    i = stride // 2
    while len(chosen) < COUNT and i < len(files):
        if files[i] not in chosen:
            chosen.append(files[i])
        i += stride

    pal = tzrle.load_game_pal(os.path.join(root, 'IMAGES', 'GAME.PAL'))
    os.makedirs(out, exist_ok=True)
    entries = []
    for n, rel in enumerate(chosen):
        s = tzrle.load(os.path.join(root, rel))
        player = n % 8
        unit = rel.upper().startswith('IMAGES/UNIT/')
        rgba = tzrle.to_rgba(s, tzrle.player_ramp(pal, player), mark_shadow=unit)
        name = rel.replace('/', '__')[:-4] + '.rgba'
        with open(os.path.join(out, name), 'wb') as f:
            f.write(rgba)
        entries.append(dict(source=rel, file=name, width=s.width, height=s.height, cellHeight=s.cell_h,
                            cells=s.dirs * s.frames, kind=s.kind, player=player, markShadow=unit))
        print('%-50s kind %d  %4d x %5d  player %d  shadow %s' % (rel, s.kind, s.width, s.height, player, unit))
    with open(os.path.join(out, 'goldens.json'), 'w') as f:
        json.dump(entries, f, indent=1)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
