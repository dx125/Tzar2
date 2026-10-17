using System;
using Godot;
using RS = Godot.RenderingServer;

namespace Tzar2.Spikes.S1;

/// <summary>
/// Canvas (2D) path: RenderingServer canvas items holding MultiMeshes. Painter's order = instance order in the buffer,
/// established by a CPU radix sort every frame. Instance stride: Transform2D (8 floats) + custom data (4 floats).
/// </summary>
public sealed class SpriteBatch2D : SpriteBatch
{
    private const int Stride = 12;

    private readonly Node _host;
    private readonly ArrayMesh _quad;
    private readonly Rid _root;
    private readonly Rid _ground;
    private readonly Rid _objectsItem;
    private readonly Rid _objects;
    private readonly ShaderMaterial _objectsMaterial;
    private readonly float[] _objectBuffer;
    private readonly Rid _shadowItem;
    private readonly Rid _shadows;
    private readonly ShaderMaterial? _shadowMaterial;
    private readonly float[]? _shadowBuffer;
    private readonly SubViewport? _maskViewport;
    private readonly Rid _maskRoot;
    private readonly Rid _compositeItem;
    private readonly ShaderMaterial? _compositeMaterial;

    public SpriteBatch2D(Node host, BatchSettings settings, SpriteSheet sheet)
        : base(settings, sheet)
    {
        _host = host;
        _quad = UnitQuad(false);
        int k = settings.Scale;
        var logical = new Vector2(settings.ViewSize.X / (float)k, settings.ViewSize.Y / (float)k);
        Rid canvas = host.GetViewport().FindWorld2D().Canvas;

        _root = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_root, canvas);
        RS.CanvasItemSetTransform(_root, Transform2D.Identity.Scaled(new Vector2(k, k)));

        if (settings.Ground)
        {
            _ground = RS.CanvasItemCreate();
            RS.CanvasItemSetParent(_ground, _root);
            RS.CanvasItemSetDefaultTextureFilter(_ground, RS.CanvasItemTextureFilter.Nearest);
            RS.CanvasItemSetDefaultTextureRepeat(_ground, RS.CanvasItemTextureRepeat.Enabled);
            RS.CanvasItemAddTextureRect(_ground, new Rect2(Vector2.Zero, logical), GroundTexture.GetRid(), true);
        }

        if (settings.Shadows == ShadowMode.Direct)
        {
            _shadowMaterial = Material(Shaders.Canvas(settings.Atlas, Shaders.Pass.ShadowDirect));
            (_shadowItem, _shadows) = CreateMultiMeshItem(_root, _shadowMaterial, settings.Capacity);
            _shadowBuffer = new float[settings.Capacity * Stride];
        }
        else if (settings.Shadows == ShadowMode.Mask)
        {
            // Shadow silhouettes go into a transparent SubViewport as opaque white, so overlaps cannot accumulate; the
            // mask is then applied once over the ground with the darkening factor.
            _maskViewport = new SubViewport
            {
                Size = settings.ViewSize,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Disable3D = true,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };
            host.AddChild(_maskViewport);
            _maskRoot = RS.CanvasItemCreate();
            RS.CanvasItemSetParent(_maskRoot, _maskViewport.FindWorld2D().Canvas);
            RS.CanvasItemSetTransform(_maskRoot, Transform2D.Identity.Scaled(new Vector2(k, k)));
            _shadowMaterial = Material(Shaders.Canvas(settings.Atlas, Shaders.Pass.ShadowMask));
            (_shadowItem, _shadows) = CreateMultiMeshItem(_maskRoot, _shadowMaterial, settings.Capacity);
            _shadowBuffer = new float[settings.Capacity * Stride];

            _compositeItem = RS.CanvasItemCreate();
            RS.CanvasItemSetParent(_compositeItem, _root);
            _compositeMaterial = new ShaderMaterial { Shader = Shaders.CanvasShadowComposite() };
            RS.CanvasItemSetMaterial(_compositeItem, _compositeMaterial.GetRid());
            RS.CanvasItemSetDefaultTextureFilter(_compositeItem, RS.CanvasItemTextureFilter.Nearest);
            RS.CanvasItemAddTextureRect(_compositeItem, new Rect2(Vector2.Zero, logical), _maskViewport.GetTexture().GetRid());
        }

        _objectsMaterial = Material(Shaders.Canvas(settings.Atlas, Shaders.Pass.Sprite));
        (_objectsItem, _objects) = CreateMultiMeshItem(_root, _objectsMaterial, settings.Capacity);
        _objectBuffer = new float[settings.Capacity * Stride];
    }

    public override string SortName => "cpu-radix";

    public override string ShadowName => Settings.Shadows.ToString().ToLowerInvariant();

    public override void Order(Entities e) => e.Sort();

    public override void Fill(Entities e)
    {
        float[] b = _objectBuffer;
        int[] order = e.Order;
        float[] xs = e.X;
        float[] ys = e.Y;
        ushort[] frames = e.Frame;
        byte[] players = e.Player;
        const float cell = SpriteSheet.Cell;
        int o = 0;
        for (int i = 0; i < e.Count; i++)
        {
            int id = order[i];

            // Snap to whole logical pixels (REQ-GFX-01); the root transform applies the integer world scale.
            float px = MathF.Round(xs[id]) - SpriteSheet.PivotX;
            float py = MathF.Round(ys[id]) - SpriteSheet.PivotY;
            b[o] = cell;
            b[o + 1] = 0;
            b[o + 2] = 0;
            b[o + 3] = px;
            b[o + 4] = 0;
            b[o + 5] = cell;
            b[o + 6] = 0;
            b[o + 7] = py;
            b[o + 8] = frames[id];
            b[o + 9] = players[id];
            b[o + 10] = 0;
            b[o + 11] = 0;
            o += Stride;
        }

        if (_shadowBuffer is null)
        {
            return;
        }

        // Shadows: the same frame flattened to half height and sheared, anchored on the foot line. Order is irrelevant.
        float[] s = _shadowBuffer;
        const float halfCell = SpriteSheet.Cell / 2f;
        float ox = MathF.Round(ShadowShear * SpriteSheet.PivotY / cell);
        float oy = MathF.Round(halfCell * SpriteSheet.PivotY / cell);
        o = 0;
        for (int id = 0; id < e.Count; id++)
        {
            float fx = MathF.Round(xs[id]) - SpriteSheet.PivotX;
            float fy = MathF.Round(ys[id]);
            s[o] = cell;
            s[o + 1] = -ShadowShear;
            s[o + 2] = 0;
            s[o + 3] = fx + ox;
            s[o + 4] = 0;
            s[o + 5] = halfCell;
            s[o + 6] = 0;
            s[o + 7] = fy - oy;
            s[o + 8] = frames[id];
            s[o + 9] = players[id];
            s[o + 10] = 1;
            s[o + 11] = 0;
            o += Stride;
        }
    }

    public override void Upload(int count)
    {
        RS.MultimeshSetBuffer(_objects, new ReadOnlySpan<float>(_objectBuffer, 0, count * Stride));
        if (_shadowBuffer is not null)
        {
            RS.MultimeshSetBuffer(_shadows, new ReadOnlySpan<float>(_shadowBuffer, 0, count * Stride));
        }
    }

    protected override void Dispose(bool disposing)
    {
        foreach (Rid rid in new[] { _objectsItem, _objects, _shadowItem, _shadows, _compositeItem, _ground, _maskRoot, _root })
        {
            if (rid.IsValid)
            {
                RS.FreeRid(rid);
            }
        }

        if (_maskViewport is not null)
        {
            _host.RemoveChild(_maskViewport);
            _maskViewport.QueueFree();
        }
    }

    private (Rid Item, Rid MultiMesh) CreateMultiMeshItem(Rid parent, ShaderMaterial material, int capacity)
    {
        Rid mm = RS.MultimeshCreate();
        RS.MultimeshSetMesh(mm, _quad.GetRid());
        RS.MultimeshAllocateData(mm, capacity, RS.MultimeshTransformFormat.Transform2D, false, true);

        // A fixed AABB keeps the server from recomputing bounds on every buffer upload.
        RS.MultimeshSetCustomAabb(mm, new Aabb(new Vector3(-4096, -4096, -1), new Vector3(16384, 16384, 2)));
        Rid item = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(item, parent);
        RS.CanvasItemSetMaterial(item, material.GetRid());
        RS.CanvasItemAddMultimesh(item, mm, default);
        return (item, mm);
    }
}
