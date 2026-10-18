// Applies the single-channel shadow mask once (TDD §13.3 shadow pass, ADR 0010 decision 4): overlapping shadows do
// not darken twice. Factor measured (UNK-03, capture 2026-10-18, ADR 0111): the original halves every 5-bit channel
// (c >> 1); this 50 % blend on the 8-bit expanded colour differs from it by at most one 5-bit level.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform float strength = 0.5;

void fragment() {
	COLOR = vec4(0.0, 0.0, 0.0, texture(TEXTURE, UV).a * strength);
}
