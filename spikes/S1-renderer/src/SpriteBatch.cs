using System;
using Godot;

namespace Tzar2.Spikes.S1;

/// <summary>How shadows are drawn.</summary>
public enum ShadowMode
{
    Off,

    /// <summary>One darkening MultiMesh drawn before the objects (overlapping shadows double-darken).</summary>
    Direct,

    /// <summary>Silhouettes into a SubViewport mask, composited once (TDD §13.3; 2D path only).</summary>
    Mask,
}

/// <summary>Settings shared by both sprite batch implementations.</summary>
public sealed record BatchSettings(bool Atlas, ShadowMode Shadows, bool Ground, int Scale, Vector2I ViewSize, int Capacity);

/// <summary>
/// One instanced sprite batch: all objects in one MultiMesh (one draw call), shadows in a second one.
/// The frame loop calls <see cref="Order"/>, <see cref="Fill"/> and <see cref="Upload"/> separately so they can be timed.
/// </summary>
public abstract class SpriteBatch : IDisposable
{
    /// <summary>Shadow shear in logical pixels at the top of the (half-height) shadow quad.</summary>
    protected const int ShadowShear = 20;

    protected SpriteBatch(BatchSettings settings, SpriteSheet sheet)
    {
        Settings = settings;
        ColorTexture = settings.Atlas ? sheet.CreateColorAtlas() : sheet.CreateColorArray();
        MaskTexture = settings.Atlas ? sheet.CreateMaskAtlas() : sheet.CreateMaskArray();
        RampTexture = sheet.CreateRampTexture();
        GroundTexture = sheet.CreateGroundTexture();
    }

    /// <summary>"cpu-radix" or "depth".</summary>
    public abstract string SortName { get; }

    public abstract string ShadowName { get; }

    public BatchSettings Settings { get; }

    protected Texture ColorTexture { get; }

    protected Texture MaskTexture { get; }

    protected Texture2D RampTexture { get; }

    protected Texture2D GroundTexture { get; }

    /// <summary>Establishes the draw order (CPU sort, or nothing for the depth path).</summary>
    public abstract void Order(Entities e);

    /// <summary>Writes the per-instance buffers.</summary>
    public abstract void Fill(Entities e);

    /// <summary>Hands the buffers to the RenderingServer.</summary>
    public abstract void Upload(int count);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);

    protected ShaderMaterial Material(Shader shader)
    {
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("color_tex", ColorTexture);
        m.SetShaderParameter("mask_tex", MaskTexture);
        m.SetShaderParameter("ramp_tex", RampTexture);
        if (Settings.Atlas)
        {
            m.SetShaderParameter("atlas_columns", SpriteSheet.AtlasColumns);
            m.SetShaderParameter("cell_uv", new Vector2(1f / SpriteSheet.AtlasColumns, 1f / SpriteSheet.AtlasRows));
        }

        return m;
    }

    /// <summary>Unit quad (0..1) with matching UVs, as a 2D (Vector2) or 3D (Vector3, y down as -y) mesh.</summary>
    protected static ArrayMesh UnitQuad(bool threeD)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        if (threeD)
        {
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, -1, 0), new(0, -1, 0) };
        }
        else
        {
            arrays[(int)Mesh.ArrayType.Vertex] = uv;
        }

        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = new int[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
            threeD ? 0 : Mesh.ArrayFormat.FlagUse2DVertices);
        return mesh;
    }
}
