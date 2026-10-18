using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Content.Editing;
using Tzar2.Import.Editing;

namespace Tzar2.Client.Editor;

/// <summary>The §18.3 SHOULDs of the editor: the read-only rules browser ("Mod Studio") and the "authentic" image import.</summary>
public partial class EditorScreen
{
    private void StudioMenu(PopupMenu map)
    {
        Item(map, "Import image with authentic conversion…", () => Files.Open(this, ["*.png, *.bmp, *.jpg ; Images"], paths => ImportAuthentic(paths[0]), many: false));
        Item(map, "Rules browser (read only)…", OpenRulesBrowser);
    }

    /// <summary>Imports an image quantised to RGB555, checks it against the terrain's colours, and adds a shadow sprite.</summary>
    public string ImportAuthentic(string path)
    {
        var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
        {
            Status("Cannot read " + path);
            return string.Empty;
        }

        image.Convert(Image.Format.Rgba8);
        var palette = new List<(byte, byte, byte)>();
        foreach (Color c in (_view.TileColours ?? []).Distinct().Take(256))
        {
            palette.Add(((byte)c.R8, (byte)c.G8, (byte)c.B8));
        }

        AuthenticResult result = AuthenticImage.Convert(image.GetData(), image.GetWidth(), image.GetHeight(), palette, shadow: true);
        string target = AssetEdits.PathFor(Path.ChangeExtension(path, ".png"));
        byte[] png = Image.CreateFromData(result.Width, result.Height, false, Image.Format.Rgba8, result.Rgba).SavePngToBuffer();
        byte[]? shadow = result.Shadow is { } s ? Image.CreateFromData(result.Width, result.Height, false, Image.Format.Rgba8, s).SavePngToBuffer() : null;
        Document.Apply("Import image (authentic)", st =>
        {
            st = st.Import(target, png);
            return shadow is null ? st : st.Import(target[..^4] + "_s.png", shadow);
        });
        Status($"Imported {target} in RGB555 with a shadow sprite; {result.OutsidePalette} pixel(s) outside the terrain set's colours.");
        return target;
    }

    private void OpenRulesBrowser()
    {
        Forms.Dialog(this, "Rules browser (read only)", new Vector2I(900, 640), body =>
        {
            var list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(840, 480) };
            void Fill(string filter)
            {
                list.Clear();
                foreach (RulesRow r in RulesBrowser.Rows(_rules, filter))
                {
                    list.AddItem($"{r.Key}  [{r.Kind}{(r.Civilization.Length > 0 ? ", " + r.Civilization : string.Empty)}]  {r.Summary}");
                }
            }

            GridContainer g = Forms.Grid(body);
            Forms.Text(g, "Filter", string.Empty, Fill);
            body.AddChild(list);
            Forms.Label(body, "The compiled ruleset (engine defaults, the imported tables and enabled mods). Editing rules is Mod Studio work after 1.0.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
            Fill(string.Empty);
        });
    }
}
