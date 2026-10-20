// World sprites drawn additively (black adds nothing): the original's fire, explosions and magic, painted on black.
// Same instances and decoding as sprite.gdshader.
shader_type canvas_item;
render_mode unshaded, blend_add;

#include "res://shaders/sprite_body.gdshaderinc"
