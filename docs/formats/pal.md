# `GAME.PAL` — player colour ramps

Status: **resolved** (layout and meaning from data; on-screen confirmation by capture pending).
Resolves the data side of **UNK-01a**. Reader: `load_game_pal` / `player_ramp` in
[`tools/re/tzrle.py`](../../tools/re/tzrle.py).

File: `IMAGES/GAME.PAL`, 1,036 bytes.

| Offset | Type | Value |
|---|---|---|
| 0 | char[4] | `PALF` |
| 4 | uint16 | entry count = 256 |
| 6 | uint16 | 0 |
| 8 | uint32 | 0 |
| 12 | 256 × 4 bytes | entries **B, G, R, 0** (same order as RLE palettes) |

Entries 0–111 are black (unused). Entries **112–255 are nine 16-step ramps**, dark → bright:

| Entries | Ramp | Player (COLOR BARS order) |
|---|---|---|
| 112–127 | blue (0,13,49) … (43,113,255) | 1 |
| 128–143 | red (50,0,0) … (230,2,2) | 2 |
| 144–159 | green (7,47,8) … (60,118,21) | 3 |
| 160–175 | purple (50,0,50) … (229,0,229) | 4 |
| 176–191 | yellow (110,112,0) … (253,255,30) | 5 |
| 192–207 | cyan (0,65,65) … (0,231,231) | 6 |
| 208–223 | orange (66,38,3) … (255,146,4) | 7 |
| 224–239 | neutral grey (32,32,33) … (90,89,92) | 8 |
| 240–255 | reference grey 23 … 255 | — (the ramp sprites are authored in) |

(RGB values given as R,G,B after reordering.)

Recolouring rule: a sprite pixel with palette index `240 + k` (k = 0…15), in an indexed
RLE sheet or a building `_c` mask, is drawn with `GAME.PAL[112 + 16 × player + k]`,
`player` = 0…7. The ramp order matches the eight bars of
`IMAGES/SCREENS/COMMON/COLOR BARS.BMP` (blue, red, green, purple, yellow, cyan, orange,
grey), whose flat colours are presumably the minimap/UI colours of the same players.

Every full indexed sprite palette carries copies of entries 224–255 at the same indices,
which is why sprites render plausibly even without recolouring.

Slot → ramp: **VERIFIED as the identity** by capture capture-20261018-024524 (ADR 0111). The kingdom colour squares of the
setup screen (kingdoms 1, 2) and of the diplomacy dialog (kingdoms 2–8) are the flat `COLOR BARS` colours, each
nearest to ramp N for kingdom N (margin ≥ 1.39 over the next ramp); kingdom 1's castle carries ramp 1 (582 blue
ramp pixels against 33 of any other). The sprite ramps of kingdoms 2–8 are confirmed by the next session's
scenario `player-colours`. Still open: the colours of the "Rescue"/"Nature" pseudo-players (RE-09).
For REQ-GFX-04 the shader takes the ramp index and these 8 × 16 colours (RGB555-expanded
values of the original ramps are exact; the ramps are already 8-bit in the file).
