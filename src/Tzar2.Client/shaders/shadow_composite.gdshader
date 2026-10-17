// Applies the single-channel shadow mask once (TDD §13.3 shadow pass, ADR 0010 decision 4): overlapping shadows do
// not darken twice. The darkening factor is a placeholder until UNK-03 is measured from the original.
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform float strength = 0.5;

void fragment() {
	COLOR = vec4(0.0, 0.0, 0.0, texture(TEXTURE, UV).a * strength);
}
