using System.Linq;
using System.Text;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;

namespace Tzar2.Client.Tests;

/// <summary>
/// Stand-ins for the menu, dialog and HUD art and the three bitmap fonts of the UI (ADR 0200), so that the UI smoke test
/// lays the screens out in CI as it would with the original's package. Only the sizes are the original's (numbers,
/// measured on the GOG v1.10 install, REQ-LEGAL-01); the pictures are empty frames and the glyphs plain blocks of the
/// original fonts' average widths.
/// </summary>
internal static class SyntheticUi
{
    /// <summary>The sizes of the art the client lays its screens out on.</summary>
    public static readonly (string Path, int Width, int Height)[] Art =
    [
        ("art/screens/common/btn left.t2img", 81, 27),
        ("art/screens/common/btn right.t2img", 81, 27),
        ("art/screens/common/chk.t2img", 31, 62),
        ("art/screens/d_advanced/back.t2img", 600, 380),
        ("art/screens/d_diplomacy/back.t2img", 500, 420),
        ("art/screens/d_diplomacy/btn.t2img", 189, 27),
        ("art/screens/d_diplomacy/row.t2img", 174, 32),
        ("art/screens/d_endgame/back2.t2img", 500, 415),
        ("art/screens/d_endgame/btn.t2img", 420, 27),
        ("art/screens/d_load/back.t2img", 550, 320),
        ("art/screens/d_load/btn.t2img", 274, 27),
        ("art/screens/d_menu/back.t2img", 291, 413),
        ("art/screens/d_message/back.t2img", 500, 320),
        ("art/screens/d_message/btn.t2img", 420, 27),
        ("art/screens/d_options/back.t2img", 800, 600),
        ("art/screens/d_options/btn.t2img", 420, 27),
        ("art/screens/d_options/check.t2img", 19, 38),
        ("art/screens/d_quests/back.t2img", 500, 368),
        ("art/screens/d_quests/btn.t2img", 420, 27),
        ("art/screens/d_save/back.t2img", 550, 320),
        ("art/screens/d_save/btn.t2img", 274, 27),
        ("art/screens/d_save/new.t2img", 210, 27),
        ("art/screens/d_say/back.t2img", 300, 140),
        ("art/screens/lose.t2img", 155, 225),
        ("art/screens/s_campaign/back.t2img", 800, 600),
        ("art/screens/s_campaign/btn.t2img", 470, 25),
        ("art/screens/s_credits/back.t2img", 800, 600),
        ("art/screens/s_main/back.t2img", 800, 600),
        ("art/screens/s_main/btn.t2img", 470, 25),
        ("art/screens/s_scrplay/back.t2img", 800, 600),
        ("art/screens/s_scrplay/ok.t2img", 600, 25),
        ("art/screens/s_singlechoosemap/back.t2img", 800, 600),
        ("art/screens/s_singlechoosemap/btn.t2img", 470, 25),
        ("art/screens/s_singleplayer/back.t2img", 800, 600),
        ("art/screens/s_singleplayer/btn.t2img", 470, 25),
        ("art/screens/s_singlernd/back.t2img", 800, 600),
        ("art/screens/s_singlernd/btn.t2img", 420, 27),
        ("art/screens/s_singlesetup/back.t2img", 800, 600),
        ("art/screens/s_singlesetup/btn.t2img", 420, 27),
        ("art/screens/s_view/btn chat left.t2img", 122, 25),
        ("art/screens/s_view/btn diplomacy left.t2img", 122, 25),
        ("art/screens/s_view/btn menu top.t2img", 228, 25),
        ("art/screens/s_view/screen 1024.t2img", 1024, 768),
        ("art/screens/waitscr/screen.t2img", 800, 600),
        ("art/screens/win.t2img", 155, 225),
    ];

    /// <summary>
    /// The three fonts of the UI: path, cell height, and the widths of a lower-case letter, a capital, a digit and a
    /// space (the original's averages, rounded).
    /// </summary>
    public static readonly (string Path, int Height, int Lower, int Upper, int Digit, int Space)[] Fonts =
    [
        ("fonts/arial10_aa", 13, 6, 8, 7, 3),
        ("fonts/arial12b_aa", 14, 7, 9, 7, 4),
        ("fonts/arial14b_aa", 17, 8, 10, 8, 4),
    ];

    /// <summary>Adds the stand-ins to <paramref name="writer"/> (paths it already has are kept).</summary>
    public static void AddTo(PackageWriter writer)
    {
        Rgba32[] palette = new Rgba32[256];
        palette[1] = new Rgba32(90, 90, 70, 255);
        foreach ((string path, int w, int h) in Art)
        {
            if (writer.Contains(path))
            {
                continue;
            }

            byte[] pixels = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[(y * w) + x] = (byte)(x < 2 || y < 2 || x >= w - 2 || y >= h - 2 ? 1 : 0);
                }
            }

            writer.Add(path, new ImageAsset { Format = PixelFormat.Indexed8, Width = w, Height = h, Palette = palette, Pixels = pixels }.ToBytes());
        }

        foreach ((string path, int height, int lower, int upper, int digit, int space) in Fonts)
        {
            // Printable ASCII, one glyph each, a solid block of the class's width.
            int[] codes = [.. Enumerable.Range(32, 95)];
            int cell = upper + 2;
            byte[] strip = new byte[cell * height * codes.Length];
            var characters = new StringBuilder();
            for (int g = 0; g < codes.Length; g++)
            {
                int c = codes[g];
                int width = c == ' ' ? space : char.IsLower((char)c) ? lower : char.IsUpper((char)c) ? upper : char.IsDigit((char)c) ? digit : lower;
                for (int y = 2; y < height - 3 && c != ' '; y++)
                {
                    for (int x = 0; x < width - 1; x++)
                    {
                        strip[(((g * height) + y) * cell) + x] = 255;
                    }
                }

                characters.Append(g == 0 ? string.Empty : ",").Append($"[{c},{c},{g},{width}]");
            }

            writer.Add(path + ".t2img", new ImageAsset { Format = PixelFormat.R8, Width = cell, Height = height * codes.Length, Pixels = strip }.ToBytes());
            writer.AddText(path + ".json", $"{{\"strip\":\"{path}.t2img\",\"face\":\"{path[6..]}\",\"cellWidth\":{cell},\"cellHeight\":{height},\"glyphCount\":{codes.Length},\"characters\":[{characters}]}}");
        }
    }
}
