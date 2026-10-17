using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Tzar2.Content.Assets;

/// <summary>
/// The eight original player-colour ramps from <c>palettes/game.json</c> (imported <c>IMAGES/GAME.PAL</c>,
/// docs/formats/pal.md, REQ-GFX-04, UNK-01a). Ramp <c>p</c>, step <c>k</c> is entry <c>RampBase + 16·p + k</c>; a sprite
/// pixel in the player range (index <c>playerFirst + k</c>) is drawn with it. No hand-picked colours.
/// </summary>
public sealed class PlayerPalette
{
    /// <summary>Number of player ramps.</summary>
    public const int Players = 8;

    /// <summary>Steps per ramp.</summary>
    public const int RampLength = 16;

    /// <summary>Package path of the palette.</summary>
    public const string PackagePath = "palettes/game.json";

    private PlayerPalette(Rgba32[] entries, int rampBase)
    {
        Entries = entries;
        RampBase = rampBase;
    }

    /// <summary>All 256 palette entries (opaque).</summary>
    public IReadOnlyList<Rgba32> Entries { get; }

    /// <summary>First entry of player 0's ramp (112 in the original).</summary>
    public int RampBase { get; }

    /// <summary>Step <paramref name="k"/> (0 = darkest) of player <paramref name="player"/>'s ramp.</summary>
    public Rgba32 Ramp(int player, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(player);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(player, Players);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(k, RampLength);
        return Entries[RampBase + (RampLength * player) + k];
    }

    /// <summary>The 16 colours of one player's ramp.</summary>
    public Rgba32[] RampOf(int player)
    {
        var ramp = new Rgba32[RampLength];
        for (int k = 0; k < RampLength; k++)
        {
            ramp[k] = Ramp(player, k);
        }

        return ramp;
    }

    /// <summary>The ramps as a 16 × 8 RGBA8 image (x = step, y = player), the shader's ramp texture.</summary>
    public byte[] ToRampTexture()
    {
        byte[] rgba = new byte[RampLength * Players * 4];
        for (int p = 0; p < Players; p++)
        {
            for (int k = 0; k < RampLength; k++)
            {
                Rgba32 c = Ramp(p, k);
                int o = ((p * RampLength) + k) * 4;
                rgba[o] = c.R;
                rgba[o + 1] = c.G;
                rgba[o + 2] = c.B;
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>Parses <c>palettes/game.json</c>: <c>entries</c> (256 × <c>rrggbb</c>) and <c>playerRampBase</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON does not describe a 256-entry palette with 8 ramps.</exception>
    public static PlayerPalette Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement root = doc.RootElement;
            JsonElement list = root.GetProperty("entries");
            if (list.GetArrayLength() != 256)
            {
                throw new InvalidDataException("game.json: expected 256 entries.");
            }

            var entries = new Rgba32[256];
            int i = 0;
            foreach (JsonElement e in list.EnumerateArray())
            {
                string hex = e.GetString() ?? string.Empty;
                if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
                {
                    throw new InvalidDataException($"game.json: entry {i} '{hex}' is not rrggbb.");
                }

                entries[i++] = new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            }

            int rampBase = root.TryGetProperty("playerRampBase", out JsonElement b) ? b.GetInt32() : 112;
            if (rampBase < 0 || rampBase + (Players * RampLength) > 256)
            {
                throw new InvalidDataException("game.json: player ramps do not fit the palette.");
            }

            return new PlayerPalette(entries, rampBase);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("game.json: " + e.Message, e);
        }
    }
}
