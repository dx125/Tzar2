using Godot;

namespace Tzar2.Spikes.S1;

/// <summary>Builds the spike's shader variants from <c>shaders/sprite_common.gdshaderinc</c>.</summary>
public static class Shaders
{
    /// <summary>What a sprite material draws.</summary>
    public enum Pass
    {
        /// <summary>Opaque sprite colour (alpha test at 0.5).</summary>
        Sprite,

        /// <summary>Darkening shadow drawn directly over the ground (overlaps double-darken).</summary>
        ShadowDirect,

        /// <summary>Opaque white silhouette into the single-channel shadow mask (overlaps do not accumulate).</summary>
        ShadowMask,
    }

    public const float ShadowStrength = 0.5f;

    private const string Include = "#include \"res://shaders/sprite_common.gdshaderinc\"\n";

    public static Shader Canvas(bool atlas, Pass pass)
    {
        string output = pass switch
        {
            Pass.Sprite => "COLOR = vec4(c.rgb, 1.0);",
            Pass.ShadowDirect => "COLOR = vec4(0.0, 0.0, 0.0, " + Float(ShadowStrength) + ");",
            _ => "COLOR = vec4(1.0);",
        };
        string code = "shader_type canvas_item;\nrender_mode unshaded, blend_mix;\n"
            + "#define COLOR_HINTS filter_nearest, repeat_disable\n"
            + (atlas ? "#define USE_ATLAS\n" : string.Empty)
            + Include
            + "varying flat vec4 v_data;\n"
            + "void vertex() { v_data = INSTANCE_CUSTOM; }\n"
            + "void fragment() {\n"
            + "\tvec4 c = sprite_texel(UV, v_data.x, v_data.y);\n"
            + "\tif (c.a < 0.5) { discard; }\n"
            + "\t" + output + "\n}\n";
        return Make(code);
    }

    /// <summary>Applies the shadow mask once with the darkening factor (TDD §13.3 shadow pass).</summary>
    public static Shader CanvasShadowComposite()
        => Make("shader_type canvas_item;\nrender_mode unshaded, blend_mix;\n"
            + "void fragment() { COLOR = vec4(0.0, 0.0, 0.0, texture(TEXTURE, UV).a * " + Float(ShadowStrength) + "); }\n");

    /// <summary>
    /// 3D variant used by the depth-sorted path: an orthographic camera, z = foot line, depth test, alpha scissor.
    /// Colour samplers carry <c>source_color</c> so the sRGB -> linear -> sRGB round trip is the identity.
    /// </summary>
    public static Shader Spatial(bool atlas, Pass pass)
    {
        bool shadow = pass != Pass.Sprite;
        string mode = shadow
            ? "render_mode unshaded, cull_disabled, blend_mix, depth_draw_never, shadows_disabled, fog_disabled;\n"
            : "render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled, fog_disabled;\n";
        string output = shadow
            ? "ALBEDO = vec3(0.0); ALPHA = " + Float(ShadowStrength) + ";"
            : "ALBEDO = c.rgb;";
        string code = "shader_type spatial;\n" + mode
            + "#define COLOR_HINTS source_color, filter_nearest, repeat_disable\n"
            + (atlas ? "#define USE_ATLAS\n" : string.Empty)
            + Include
            + "varying flat vec4 v_data;\n"
            + "void vertex() { v_data = INSTANCE_CUSTOM; }\n"
            + "void fragment() {\n"
            + "\tvec4 c = sprite_texel(UV, v_data.x, v_data.y);\n"
            + "\tif (c.a < 0.5) { discard; }\n"
            + "\t" + output + "\n}\n";
        return Make(code);
    }

    public static Shader SpatialGround()
        => Make("shader_type spatial;\nrender_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;\n"
            + "uniform sampler2D ground_tex : source_color, filter_nearest, repeat_enable;\n"
            + "uniform vec2 tiles = vec2(1.0);\n"
            + "void fragment() { ALBEDO = texture(ground_tex, UV * tiles).rgb; }\n");

    private static string Float(float f) => f.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    private static Shader Make(string code) => new() { Code = code };
}
