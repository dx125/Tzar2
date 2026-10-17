// Draws an RGB5A1 surface uploaded losslessly as RG8 (ADR 0013), one animation frame per array layer: composed terrain
// and raw terrain bitmaps. Nearest sampling via texelFetch; the expansion is the bit replication of TDD §13.2.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2DArray frames : filter_nearest, repeat_disable;
uniform int frame = 0;
uniform vec2 size = vec2(1.0);
// Raw bitmaps: draw colour-key pixels (bit 15 clear) with their own colour, so magenta/green keys are visible.
uniform bool show_keys = false;

float expand5(int c) {
	return float((c << 3) | (c >> 2)) / 255.0;
}

void fragment() {
	ivec2 p = clamp(ivec2(floor(UV * size)), ivec2(0), ivec2(size) - 1);
	vec2 rg = texelFetch(frames, ivec3(p, frame), 0).rg;
	int word = int(rg.r * 255.0 + 0.5) | (int(rg.g * 255.0 + 0.5) << 8);
	if ((word & 0x8000) == 0 && !show_keys) {
		discard;
	}
	int c = word & 0x7FFF;
	COLOR = vec4(expand5((c >> 10) & 31), expand5((c >> 5) & 31), expand5(c & 31), 1.0);
}
