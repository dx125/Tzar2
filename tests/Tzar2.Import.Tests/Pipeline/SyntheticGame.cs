using System;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Tests.Shared;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>A miniature install with one file of every kind the importer handles (synthetic, REQ-LEGAL-03).</summary>
internal sealed class SyntheticGame : IDisposable
{
    public SyntheticGame()
    {
        Root = Path.Combine(Path.GetTempPath(), "tzar2-game-" + Guid.NewGuid().ToString("N"));
        var palette = Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)(i / 3), (byte)(255 - i))).ToArray();
        palette[20] = (0, 9, 0); // a baked-shadow colour

        Write("IMAGES/GAME.PAL", SyntheticFormats.Pal());
        Write("IMAGES/UNIT/EURO ARCHER.RLE", SyntheticFormats.Rle(1, 2, 2, 12, 10,
            (c, x, y) => x > 2 && y > c ? (x == 4 ? 20 : x == 5 ? 243 : 100 + x + y) : null, palette, key: 0));
        Write("IMAGES/UNIT/EURO ARCHER.DAT", SyntheticFormats.Dat((0, 0, 0, 0), [], Enumerable.Repeat((short)0, 4).Select(_ => ((short)3, (short)1, (short)11, (short)9)).ToArray()));
        Write("IMAGES/UNIT/COW.RLE", SyntheticFormats.Rle(3, 1, 1, 6, 4, (c, x, y) => x == 0 ? 0x0020 : 0x1234 + x));
        Write("IMAGES/BUILDING/EURO MILL.RLE", SyntheticFormats.Rle(3, 1, 2, 40, 30, (c, x, y) => x + y > 20 ? 0x7C00 | x : null, extraRows: 1));
        Write("IMAGES/BUILDING/EURO MILL S.RLE", SyntheticFormats.Rle(7, 1, 2, 40, 30, (c, x, y) => x < y ? 1 : null));
        Write("IMAGES/BUILDING/EURO MILL.DAT", SyntheticFormats.Dat((0, 0, 63, 31), [0, 2], null));
        Write("IMAGES/ICONS/ICONS.BMP", SyntheticFormats.Bmp(2, 2, 16, [0xE0, 0x03, 0x1F, 0x7C, 0x00, 0x00, 0xFF, 0x7F]));
        Write("IMAGES/BUILDING/MASK/EURO MILL C.BMP", SyntheticFormats.Bmp(2, 1, 8, [1, 2], [(0, 0, 0), (0, 255, 0), (240, 240, 240)]));
        Write("IMAGES/TERRAIN/GRASS/11.BMP", SyntheticFormats.Bmp(2, 1, 16, [0xE0, 0x03, 0x00, 0x00]));
        Write("IMAGES/TERRAIN/GRASS/12.BMP", SyntheticFormats.Bmp(2, 1, 16, [0xE0, 0x03, 0x1F, 0x7C]));
        Write("IMAGES/TERRAIN/GRASS/LIGHT.BMP", SyntheticFormats.Bmp(1, 1, 8, [3]));
        Write("Hero Icons/Big/H1.bmp", SyntheticFormats.Bmp(1, 1, 24, [1, 2, 3]));
        Text("IMAGES/TERRAIN/GRASS.TERRAIN.INI", "[terrain]\r\nname = Grassland\r\ndir = grass\r\n[terrainname]\r\n1 = Dirt\r\n[terrainfiles]\r\n11 = 11.bmp\r\n12 = 12.bmp\r\n13 = 13.bmp\r\n[terrainsize]\r\n11 = 32,32,0\r\n12 = 2,14,0\r\n");
        Text("IMAGES/TERRAIN/GRASS.IMAGES.INI", "[g_light]\r\nimage = light.bmp\r\n");
        Text("DATA/IMAGES.INI", "[e_archer1]\r\nimage = unit\\euro archer.bmp\r\nrows = 2\r\ncols = 2\r\nactionrow = 1\r\nactionrow = 2\r\n[e_mill]\r\nimage = building\\euro mill.bmp\r\n[icons]\r\nimage = icons\\icons.bmp\r\n[gone]\r\nimage = scenery\\gone.bmp\r\n");
        Text("DATA/ICONS.INI", "[galleon]\r\nimg = 32\r\n[galleon]\r\nimg = 130\r\n");
        Text("DATA/EURO UNITS.TXT", "NAME\tCLASS\tCOST\r\nArcher\tarcher\t\"40f, 20w\"\r\n\t\t\r\n");
        Text("DATA/CHANGES.TXT", "notes ö\r\n");
        Text("DATA/AI/A.AI", "ai\r\n");
        Write("LOCAL/FONTS/SMALL.FNT", SyntheticFormats.Fnt("Small Fonts", 4, 5, 3, c => c is >= 65 and <= 67 ? (3, c - 65) : (0, 0)));
        Write("SOUND/COMMAND/YES.WAV", SyntheticFormats.Wav());
        Write("MUSIC/1.MID", SyntheticFormats.Mid());
        Write("Maps/test.wmp", SyntheticFormats.Hpfs([("MAPS/test.header", new byte[10]), ("MAPS/inner.wmp", SyntheticFormats.Hpfs([("x", [1])]))]));
        Write("MOVIES/LOGO.DDV", "DDV\0"u8.ToArray());
        Write("Tzar.exe", "MZ"u8.ToArray());
        Text("tzar.ini", "[settings]\r\n");
    }

    public string Root { get; }

    public void Write(string relative, byte[] data)
    {
        string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    public void Text(string relative, string text) => Write(relative, Encoding.Latin1.GetBytes(text));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
