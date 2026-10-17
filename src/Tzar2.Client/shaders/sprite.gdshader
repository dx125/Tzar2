// World sprites (TDD §13.2-§13.3, ADR 0010, ADR 0013). One instanced quad per sprite cell, drawn by a canvas MultiMesh
// in CPU-sorted painter's order. The decoding below is a line-by-line translation of
// Tzar2.Content.Assets.SpriteShading.Shade (the tested CPU reference); keep both in sync.
//
// INSTANCE_CUSTOM: x = global cell index, y = palette row (indexed sheets), z = player 0-7 or -1, w = unused.
// cell_tex holds two RGBA32F texels per cell: (page x, page y, width, height), (layer, format, player first, shadow RGB555).
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2DArray idx_tex : filter_nearest, repeat_disable;   // Indexed8 pages (R8 palette indices)
uniform sampler2DArray rgb_tex : filter_nearest, repeat_disable;   // RGB5A1 pages (RG8 = little-endian words)
uniform sampler2DArray rgba_tex : filter_nearest, repeat_disable;  // RGBA8 pages
uniform sampler2DArray mask_tex : filter_nearest, repeat_disable;  // R8 shadow masks (0/1)
uniform sampler2D pal_tex : filter_nearest, repeat_disable;        // 256 x rows; alpha 0 transparent, 128 baked shadow, 255 opaque
uniform sampler2D ramp_tex : filter_nearest, repeat_disable;       // 16 x 8 GAME.PAL player ramps
uniform sampler2D cell_tex : filter_nearest, repeat_disable;
uniform int cell_tex_width = 4096;
// false: colour pass (opaque pixels); true: shadow pass (white silhouettes into the single-channel shadow mask).
uniform bool shadow_pass = false;
// Treat the importer's baked-shadow hints (rle.md §5.4) as shadow instead of opaque colour (UNK-03 open).
uniform bool baked_shadow_as_shadow = true;

varying flat vec4 v_data;

void vertex() {
	v_data = INSTANCE_CUSTOM;
}

// Bit replication c8 = (c5 << 3) | (c5 >> 2) (TDD §13.2; UNK-39 keeps it swappable).
float expand5(int c) {
	return float((c << 3) | (c >> 2)) / 255.0;
}

int byte_of(float v) {
	return int(v * 255.0 + 0.5);
}

ivec2 cell_texel(int cell, int k) {
	int i = cell * 2 + k;
	return ivec2(i % cell_tex_width, i / cell_tex_width);
}

void fragment() {
	int cell = int(round(v_data.x));
	vec4 box = texelFetch(cell_tex, cell_texel(cell, 0), 0);
	vec4 info = texelFetch(cell_tex, cell_texel(cell, 1), 0);
	ivec2 size = ivec2(round(box.zw));
	ivec2 p = ivec2(round(box.xy)) + clamp(ivec2(floor(UV * box.zw)), ivec2(0), size - 1);
	int layer = int(round(info.x));
	int format = int(round(info.y));
	int kind = 0; // 0 none, 1 colour, 2 shadow
	vec3 colour = vec3(0.0);
	if (format == 1) {
		int index = byte_of(texelFetch(idx_tex, ivec3(p, layer), 0).r);
		vec4 pal = texelFetch(pal_tex, ivec2(index, int(round(v_data.y))), 0);
		int cls = byte_of(pal.a);
		colour = pal.rgb;
		if (cls == 128 && baked_shadow_as_shadow) {
			kind = 2;
		} else if (cls != 0) {
			kind = 1;
			int first = int(round(info.z));
			int player = int(round(v_data.z));
			if (player >= 0 && first >= 0 && index >= first && index < first + 16) {
				colour = texelFetch(ramp_tex, ivec2(index - first, player), 0).rgb;
			}
		}
	} else if (format == 2) {
		vec2 rg = texelFetch(rgb_tex, ivec3(p, layer), 0).rg;
		int word = byte_of(rg.r) | (byte_of(rg.g) << 8);
		if ((word & 0x8000) != 0) {
			int c = word & 0x7FFF;
			colour = vec3(expand5((c >> 10) & 31), expand5((c >> 5) & 31), expand5(c & 31));
			kind = (baked_shadow_as_shadow && c == int(round(info.w))) ? 2 : 1;
		}
	} else if (format == 3) {
		vec4 c = texelFetch(rgba_tex, ivec3(p, layer), 0);
		colour = c.rgb;
		kind = c.a > 0.0 ? 1 : 0;
	} else {
		kind = texelFetch(mask_tex, ivec3(p, layer), 0).r > 0.0 ? 2 : 0;
	}

	if (shadow_pass) {
		if (kind != 2) {
			discard;
		}
		COLOR = vec4(1.0);
	} else {
		if (kind != 1) {
			discard;
		}
		COLOR = vec4(colour, 1.0);
	}
}
