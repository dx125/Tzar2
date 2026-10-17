using System;
using Godot;
using Tzar2.Client.Logic;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Rendering;

/// <summary>One sprite cell to draw.</summary>
/// <param name="X">Left of the trimmed box in logical pixels (whole pixels, REQ-GFX-01).</param>
/// <param name="Y">Top of the trimmed box.</param>
/// <param name="Width">Trimmed width.</param>
/// <param name="Height">Trimmed height.</param>
/// <param name="Cell">Global cell index in the bound <see cref="GpuSpriteAtlas"/>.</param>
/// <param name="PaletteRow">Palette row of the cell's sheet (indexed sheets) or −1.</param>
/// <param name="Player">Player 0–7, or −1 for the sheet's own reference ramp.</param>
/// <param name="SortKey">Painter's order key (foot line and sub-order); ties keep insertion order.</param>
/// <param name="ShadowOnly">Draw only into the shadow pass (kind-7 shadow sheets).</param>
public readonly record struct SpriteInstance(int X, int Y, int Width, int Height, int Cell, int PaletteRow, int Player, uint SortKey, bool ShadowOnly);

/// <summary>
/// The production sprite path of ADR 0010: <c>RenderingServer</c> canvas items holding one MultiMesh for objects
/// (CPU radix-sorted painter's order) and one for shadow silhouettes, which go into a transparent single-channel mask
/// (<c>SubViewport</c>) that is composited once with the darkening factor, so overlapping shadows do not double-darken
/// (§13.3). No node per sprite. The integer world scale and the pan are a canvas transform; instance positions are whole
/// logical pixels.
/// </summary>
public sealed class SpriteRenderer : IDisposable
{
    private const int Stride = 12;
    private const int Chunk = 256;

    private readonly Node _host;
    private readonly ArrayMesh _quad;
    private readonly ShaderMaterial _colourMaterial;
    private readonly ShaderMaterial _shadowMaterial;
    private readonly ShaderMaterial _compositeMaterial;
    private readonly SubViewport _maskViewport;
    private readonly Rid _maskRoot;
    private readonly Rid _composite;
    private readonly Rid _objectsItem;
    private readonly Rid _objects;
    private readonly Rid _shadowsItem;
    private readonly Rid _shadows;
    private readonly RadixSort _sort = new();
    private SpriteInstance[] _instances = new SpriteInstance[Chunk];
    private uint[] _keys = new uint[Chunk];
    private int[] _order = new int[Chunk];
    private float[] _objectBuffer = new float[Chunk * Stride];
    private float[] _shadowBuffer = new float[Chunk * Stride];
    private int _capacity = Chunk;
    private int _count;
    private Vector2I _maskSize;
    private bool _visible = true;
    private bool _shadowsVisible = true;

    /// <summary>Creates the canvas items under <paramref name="parent"/>; <paramref name="drawIndex"/> orders them among siblings.</summary>
    public SpriteRenderer(Node host, Rid parent, int drawIndex)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _quad = UnitQuad();
        var shader = GD.Load<Shader>("res://shaders/sprite.gdshader");
        _colourMaterial = new ShaderMaterial { Shader = shader };
        _shadowMaterial = new ShaderMaterial { Shader = shader };
        _shadowMaterial.SetShaderParameter("shadow_pass", true);
        _compositeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/shadow_composite.gdshader") };

        _maskViewport = new SubViewport
        {
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Disable3D = true,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            Size = new Vector2I(16, 16),
        };
        host.AddChild(_maskViewport);
        _maskRoot = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_maskRoot, _maskViewport.FindWorld2D().Canvas);
        (_shadowsItem, _shadows) = CreateMultiMeshItem(_maskRoot, _shadowMaterial);

        _composite = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_composite, parent);
        RS.CanvasItemSetDrawIndex(_composite, drawIndex);
        RS.CanvasItemSetMaterial(_composite, _compositeMaterial.GetRid());
        RS.CanvasItemSetDefaultTextureFilter(_composite, RS.CanvasItemTextureFilter.Nearest);

        (_objectsItem, _objects) = CreateMultiMeshItem(parent, _colourMaterial);
        RS.CanvasItemSetDrawIndex(_objectsItem, drawIndex + 1);
    }

    /// <summary>Instances added since the last <see cref="Clear"/>.</summary>
    public int Count => _count;

    /// <summary>Binds the atlas whose global cell indices the instances use.</summary>
    public void SetAtlas(GpuSpriteAtlas? atlas)
    {
        if (atlas is not null)
        {
            atlas.Bind(_colourMaterial);
            atlas.Bind(_shadowMaterial);
        }
    }

    /// <summary>Sets the canvas transform: screen = origin + scale × (logical − pan).</summary>
    public void SetView(Transform2D view, Vector2I screenSize)
    {
        RS.CanvasItemSetTransform(_objectsItem, view);
        RS.CanvasItemSetTransform(_maskRoot, view);
        if (screenSize != _maskSize && screenSize.X > 0 && screenSize.Y > 0)
        {
            _maskSize = screenSize;
            _maskViewport.Size = screenSize;
            RS.CanvasItemClear(_composite);
            RS.CanvasItemAddTextureRect(_composite, new Rect2(Vector2.Zero, screenSize), _maskViewport.GetTexture().GetRid());
        }
    }

    /// <summary>Baked-shadow hints as shadow (true) or as opaque colour.</summary>
    public void SetBakedShadowAsShadow(bool value)
    {
        _colourMaterial.SetShaderParameter("baked_shadow_as_shadow", value);
        _shadowMaterial.SetShaderParameter("baked_shadow_as_shadow", value);
    }

    /// <summary>Shows or hides the shadow pass.</summary>
    public void SetShadowsVisible(bool value)
    {
        _shadowsVisible = value;
        RS.CanvasItemSetVisible(_composite, _visible && _shadowsVisible);
    }

    /// <summary>Shows or hides everything.</summary>
    public void SetVisible(bool value)
    {
        _visible = value;
        RS.CanvasItemSetVisible(_objectsItem, value);
        RS.CanvasItemSetVisible(_maskRoot, value);
        RS.CanvasItemSetVisible(_composite, _visible && _shadowsVisible);
    }

    /// <summary>Starts a new frame.</summary>
    public void Clear() => _count = 0;

    /// <summary>Adds an instance (insertion order breaks sort-key ties).</summary>
    public void Add(in SpriteInstance instance)
    {
        if (instance.Width <= 0 || instance.Height <= 0)
        {
            return;
        }

        if (_count == _instances.Length)
        {
            System.Array.Resize(ref _instances, _instances.Length * 2);
            System.Array.Resize(ref _keys, _keys.Length * 2);
            System.Array.Resize(ref _order, _order.Length * 2);
        }

        _instances[_count] = instance;
        _keys[_count] = instance.SortKey;
        _count++;
    }

    /// <summary>Sorts, fills and uploads the instance buffers.</summary>
    public void Commit()
    {
        if (_count > _capacity)
        {
            _capacity = ((_count + Chunk - 1) / Chunk) * Chunk;
            _objectBuffer = new float[_capacity * Stride];
            _shadowBuffer = new float[_capacity * Stride];
            RS.MultimeshAllocateData(_objects, _capacity, RS.MultimeshTransformFormat.Transform2D, false, true);
            RS.MultimeshAllocateData(_shadows, _capacity, RS.MultimeshTransformFormat.Transform2D, false, true);
        }

        _sort.Sort(_keys.AsSpan(0, _count), _order);
        int objects = 0, shadows = 0;
        for (int i = 0; i < _count; i++)
        {
            SpriteInstance s = _instances[_order[i]];
            if (s.ShadowOnly)
            {
                Write(_shadowBuffer, shadows++, s);
            }
            else
            {
                Write(_objectBuffer, objects++, s);

                // Colour sheets may carry baked shadow (unit sheets, rle.md §5.4): the shadow pass keeps only those pixels.
                Write(_shadowBuffer, shadows++, s);
            }
        }

        RS.MultimeshSetBuffer(_objects, _objectBuffer);
        RS.MultimeshSetBuffer(_shadows, _shadowBuffer);
        RS.MultimeshSetVisibleInstances(_objects, objects);
        RS.MultimeshSetVisibleInstances(_shadows, shadows);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (Rid rid in new[] { _objectsItem, _objects, _shadowsItem, _shadows, _composite, _maskRoot })
        {
            if (rid.IsValid)
            {
                RS.FreeRid(rid);
            }
        }

        _colourMaterial.Dispose();
        _shadowMaterial.Dispose();
        _compositeMaterial.Dispose();
        _quad.Dispose();
        if (GodotObject.IsInstanceValid(_maskViewport))
        {
            _host.RemoveChild(_maskViewport);
            _maskViewport.QueueFree();
        }
    }

    private static void Write(float[] b, int slot, in SpriteInstance s)
    {
        int o = slot * Stride;
        b[o] = s.Width;
        b[o + 1] = 0;
        b[o + 2] = 0;
        b[o + 3] = s.X;
        b[o + 4] = 0;
        b[o + 5] = s.Height;
        b[o + 6] = 0;
        b[o + 7] = s.Y;
        b[o + 8] = s.Cell;
        b[o + 9] = s.PaletteRow;
        b[o + 10] = s.Player;
        b[o + 11] = 0;
    }

    private (Rid Item, Rid MultiMesh) CreateMultiMeshItem(Rid parent, ShaderMaterial material)
    {
        Rid mm = RS.MultimeshCreate();
        RS.MultimeshSetMesh(mm, _quad.GetRid());
        RS.MultimeshAllocateData(mm, _capacity, RS.MultimeshTransformFormat.Transform2D, false, true);
        RS.MultimeshSetVisibleInstances(mm, 0);

        // A fixed AABB keeps the server from recomputing bounds on every buffer upload (ADR 0010).
        RS.MultimeshSetCustomAabb(mm, new Aabb(new Vector3(-65536, -65536, -1), new Vector3(131072, 131072, 2)));
        Rid item = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(item, parent);
        RS.CanvasItemSetMaterial(item, material.GetRid());
        RS.CanvasItemSetDefaultTextureFilter(item, RS.CanvasItemTextureFilter.Nearest);
        RS.CanvasItemAddMultimesh(item, mm, default);
        return (item, mm);
    }

    private static ArrayMesh UnitQuad()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        arrays[(int)Mesh.ArrayType.Vertex] = uv;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = new int[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, Mesh.ArrayFormat.FlagUse2DVertices);
        return mesh;
    }
}
