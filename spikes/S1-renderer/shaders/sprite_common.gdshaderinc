// Shared sprite sampling for the S1 spike (TDD §13.2-§13.3, REQ-GFX-04).
// The including shader defines COLOR_HINTS (sampler hints for colour data) and optionally USE_ATLAS.
// Per-instance data arrives in INSTANCE_CUSTOM: x = frame index, y = player index, z = flags, w = unused.

#ifdef USE_ATLAS
uniform sampler2D color_tex : COLOR_HINTS;
uniform sampler2D mask_tex : filter_nearest, repeat_disable;
uniform int atlas_columns = 16;
uniform vec2 cell_uv = vec2(0.0625, 0.0625);
#else
uniform sampler2DArray color_tex : COLOR_HINTS;
uniform sampler2DArray mask_tex : filter_nearest, repeat_disable;
#endif
uniform sampler2D ramp_tex : COLOR_HINTS;

// Returns the texel of frame `frame` at `uv`, with the player-colour mask applied through the ramp texture.
vec4 sprite_texel(vec2 uv, float frame, float player) {
#ifdef USE_ATLAS
	int f = int(frame + 0.5);
	vec2 cell = vec2(float(f % atlas_columns), float(f / atlas_columns));
	vec2 auv = (cell + uv) * cell_uv;
	vec4 c = texture(color_tex, auv);
	float m = texture(mask_tex, auv).r;
#else
	vec3 luv = vec3(uv, frame);
	vec4 c = texture(color_tex, luv);
	float m = texture(mask_tex, luv).r;
#endif
	if (m > 0.0) {
		int shade = int(m * 255.0 + 0.5) - 1;
		c.rgb = texelFetch(ramp_tex, ivec2(shade, int(player + 0.5)), 0).rgb;
	}
	return c;
}
