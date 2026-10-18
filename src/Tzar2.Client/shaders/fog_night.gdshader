// Fog of war and night over the world (TDD §13.6, §13.7), one quad covering the map in world pixels.
// fog_tex: one texel per tile, R = 0 unexplored, 0.5 explored, 1 visible (linear sampling softens the edges).
// light_tex: one texel per tile, R = light from own units and buildings (UNK-32 placeholder radius), linear.
// light_pattern: the terrain set's LIGHT.BMP, tiled in world pixels, modulates the light (flicker-free pattern).
// ⚠ UNK-27 (fog colour, dithering, edge softness) and UNK-32 (night rules) are open: the factors below are placeholders.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D fog_tex : filter_linear, repeat_disable;
uniform sampler2D light_tex : filter_linear, repeat_disable;
uniform sampler2D light_pattern : filter_nearest, repeat_enable;
uniform vec2 map_pixels = vec2(1024.0);
uniform float explored_alpha = 0.5;
uniform float darkness = 0.0;
uniform bool fog_enabled = true;

void fragment() {
	vec2 world = UV * map_pixels;
	float fog = 0.0;
	if (fog_enabled) {
		float v = texture(fog_tex, UV).r;
		// 0 -> 1 (black), 0.5 -> explored_alpha, 1 -> 0
		fog = v < 0.5 ? mix(1.0, explored_alpha, v * 2.0) : mix(explored_alpha, 0.0, (v - 0.5) * 2.0);
	}
	float light = clamp(texture(light_tex, UV).r, 0.0, 1.0) * mix(0.6, 1.0, texture(light_pattern, world / 512.0).r);
	float night = darkness * (1.0 - light);
	float a = 1.0 - (1.0 - fog) * (1.0 - night);
	COLOR = vec4(0.0, 0.0, 0.02 * night, a);
}
