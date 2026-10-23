// Draws a terrain chunk from the deduplicated tile atlas (ADR 0210): per tile and animation frame the chunk's index
// holds a 24-bit atlas slot (RGBA8: low, middle, high byte); the atlas holds each distinct 32 x 32 tile once as RGB5A1
// in RG8 (ADR 0013). The expansion is the bit replication of TDD §13.2, as in rgb555.gdshader, so the pixels are those
// of the composed frames.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2DArray atlas : filter_nearest, repeat_disable;
uniform sampler2DArray tiles : filter_nearest, repeat_disable;
uniform int frame = 0;
uniform vec2 size = vec2(1.0);

const int TILE = 32;
const int PAGE_TILES = 64;

float expand5(int c) {
	return float((c << 3) | (c >> 2)) / 255.0;
}

int word_of(vec2 rg) {
	return int(rg.r * 255.0 + 0.5) | (int(rg.g * 255.0 + 0.5) << 8);
}

void fragment() {
	ivec2 p = clamp(ivec2(floor(UV * size)), ivec2(0), ivec2(size) - 1);
	vec3 s = texelFetch(tiles, ivec3(p / TILE, frame), 0).rgb;
	int slot = int(s.r * 255.0 + 0.5) | (int(s.g * 255.0 + 0.5) << 8) | (int(s.b * 255.0 + 0.5) << 16);
	int local = slot % (PAGE_TILES * PAGE_TILES);
	ivec2 at = ivec2(local % PAGE_TILES, local / PAGE_TILES) * TILE + p % TILE;
	int word = word_of(texelFetch(atlas, ivec3(at, slot / (PAGE_TILES * PAGE_TILES)), 0).rg);
	if ((word & 0x8000) == 0) {
		discard;
	}
	int c = word & 0x7FFF;
	COLOR = vec4(expand5((c >> 10) & 31), expand5((c >> 5) & 31), expand5(c & 31), 1.0);
}
