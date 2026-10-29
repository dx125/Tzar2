// Fog of war over the world (TDD §13.6), one quad covering the map in world pixels. The night is a tint of its own
// (NightLight, PRES-015), drawn under this quad.
// fog_tex: one texel per tile, R = 0 unexplored, 0.5 explored, 1 visible (linear sampling softens the edges).
// ⚠ UNK-27 (fog colour, dithering, edge softness) is open: the factors below are placeholders.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D fog_tex : filter_linear, repeat_disable;
uniform float explored_alpha = 0.5;
uniform bool fog_enabled = true;

void fragment() {
	float fog = 0.0;
	if (fog_enabled) {
		float v = texture(fog_tex, UV).r;
		// 0 -> 1 (black), 0.5 -> explored_alpha, 1 -> 0
		fog = v < 0.5 ? mix(1.0, explored_alpha, v * 2.0) : mix(explored_alpha, 0.0, (v - 0.5) * 2.0);
	}
	COLOR = vec4(0.0, 0.0, 0.0, fog);
}
