// World sprites (TDD §13.2-§13.3, ADR 0010, ADR 0013). One instanced quad per sprite cell, drawn by a canvas MultiMesh
// in CPU-sorted painter's order. The decoding below is a line-by-line translation of
// Tzar2.Content.Assets.SpriteShading.Shade (the tested CPU reference); keep both in sync.
//
// INSTANCE_CUSTOM: x = global cell index, y = palette row (indexed sheets), z = player 0-7 or -1, w = unused.
// cell_tex holds two RGBA32F texels per cell: (page x, page y, width, height), (layer, format, player first, shadow RGB555).
shader_type canvas_item;
render_mode unshaded, blend_mix;

#include "res://shaders/sprite_body.gdshaderinc"
