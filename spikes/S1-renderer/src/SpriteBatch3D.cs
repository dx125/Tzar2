using System;
using Godot;
using RS = Godot.RenderingServer;

namespace Tzar2.Spikes.S1;

/// <summary>
/// Depth path: the same quads as 3D MultiMesh instances under an orthographic camera (1 world unit = 1 logical pixel,
/// world y = -screen y). z = snapped foot line, so the depth test (with alpha scissor) gives the y-order and the CPU
/// does not sort. Equal foot lines resolve by draw order (stable, but not the painter's tie order). Instance stride:
/// Transform3D (12 floats) + custom data (4 floats). Shadows are a transparent MultiMesh behind every object.
/// </summary>
public sealed class SpriteBatch3D : SpriteBatch
{
    private const int Stride = 16;
    private const float GroundZ = -50f;
    private const float ShadowZ = -10f;

    private readonly Node _host;
    private readonly ArrayMesh _quad;
    private readonly Camera3D _camera;
    private readonly Rid _objects;
    private readonly Rid _objectsInstance;
    private readonly ShaderMaterial _objectsMaterial;
    private readonly float[] _objectBuffer;
    private readonly Rid _shadows;
    private readonly Rid _shadowsInstance;
    private readonly ShaderMaterial? _shadowMaterial;
    private readonly float[]? _shadowBuffer;
    private readonly Rid _groundInstance;
    private readonly ShaderMaterial? _groundMaterial;

    public SpriteBatch3D(Node host, BatchSettings settings, SpriteSheet sheet)
        : base(settings, sheet)
    {
        _host = host;
        _quad = UnitQuad(true);
        int k = settings.Scale;
        float lw = settings.ViewSize.X / (float)k;
        float lh = settings.ViewSize.Y / (float)k;
        Rid scenario = host.GetViewport().FindWorld3D().Scenario;

        // Logical (0,0) maps exactly onto the top-left screen pixel; one world unit covers k x k screen pixels.
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = lh,
            Near = 1f,
            Far = lh + 400f,
            Position = new Vector3(lw / 2f, -lh / 2f, lh + 200f),
        };
        host.AddChild(_camera);
        _camera.MakeCurrent();

        if (settings.Ground)
        {
            _groundMaterial = new ShaderMaterial { Shader = Shaders.SpatialGround() };
            _groundMaterial.SetShaderParameter("ground_tex", GroundTexture);
            _groundMaterial.SetShaderParameter("tiles", new Vector2(lw / SpriteSheet.GroundTile, lh / SpriteSheet.GroundTile));
            _groundInstance = RS.InstanceCreate2(_quad.GetRid(), scenario);
            RS.InstanceGeometrySetMaterialOverride(_groundInstance, _groundMaterial.GetRid());
            RS.InstanceSetTransform(_groundInstance, new Transform3D(new Basis(new Vector3(lw, 0, 0), new Vector3(0, lh, 0), Vector3.Back), new Vector3(0, 0, GroundZ)));
        }

        if (settings.Shadows != ShadowMode.Off)
        {
            _shadowMaterial = Material(Shaders.Spatial(settings.Atlas, Shaders.Pass.ShadowDirect));
            (_shadows, _shadowsInstance) = CreateMultiMesh(scenario, _shadowMaterial, settings.Capacity);
            _shadowBuffer = new float[settings.Capacity * Stride];
        }

        _objectsMaterial = Material(Shaders.Spatial(settings.Atlas, Shaders.Pass.Sprite));
        (_objects, _objectsInstance) = CreateMultiMesh(scenario, _objectsMaterial, settings.Capacity);
        _objectBuffer = new float[settings.Capacity * Stride];
    }

    public override string SortName => "depth";

    // The mask mode needs a separate render target; the depth path only implements direct shadows.
    public override string ShadowName => Settings.Shadows == ShadowMode.Off ? "off" : "direct";

    public override void Order(Entities e)
    {
    }

    public override void Fill(Entities e)
    {
        float[] b = _objectBuffer;
        float[] xs = e.X;
        float[] ys = e.Y;
        ushort[] frames = e.Frame;
        byte[] players = e.Player;
        const float cell = SpriteSheet.Cell;
        int o = 0;
        for (int id = 0; id < e.Count; id++)
        {
            float fy = MathF.Round(ys[id]);
            float px = MathF.Round(xs[id]) - SpriteSheet.PivotX;
            float py = fy - SpriteSheet.PivotY;

            // Row-major 3x4: basis x = (cell, 0, 0), basis y = (0, cell, 0), basis z = (0, 0, 1), origin = (px, -py, footY).
            b[o] = cell;
            b[o + 1] = 0;
            b[o + 2] = 0;
            b[o + 3] = px;
            b[o + 4] = 0;
            b[o + 5] = cell;
            b[o + 6] = 0;
            b[o + 7] = -py;
            b[o + 8] = 0;
            b[o + 9] = 0;
            b[o + 10] = 1;
            b[o + 11] = fy;
            b[o + 12] = frames[id];
            b[o + 13] = players[id];
            b[o + 14] = 0;
            b[o + 15] = 0;
            o += Stride;
        }

        if (_shadowBuffer is null)
        {
            return;
        }

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
            s[o + 1] = ShadowShear;
            s[o + 2] = 0;
            s[o + 3] = fx + ox;
            s[o + 4] = 0;
            s[o + 5] = halfCell;
            s[o + 6] = 0;
            s[o + 7] = -(fy - oy);
            s[o + 8] = 0;
            s[o + 9] = 0;
            s[o + 10] = 1;
            s[o + 11] = ShadowZ;
            s[o + 12] = frames[id];
            s[o + 13] = players[id];
            s[o + 14] = 1;
            s[o + 15] = 0;
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
        foreach (Rid rid in new[] { _objectsInstance, _objects, _shadowsInstance, _shadows, _groundInstance })
        {
            if (rid.IsValid)
            {
                RS.FreeRid(rid);
            }
        }

        _host.RemoveChild(_camera);
        _camera.QueueFree();
    }

    private (Rid MultiMesh, Rid Instance) CreateMultiMesh(Rid scenario, ShaderMaterial material, int capacity)
    {
        Rid mm = RS.MultimeshCreate();
        RS.MultimeshSetMesh(mm, _quad.GetRid());
        RS.MultimeshAllocateData(mm, capacity, RS.MultimeshTransformFormat.Transform3D, false, true);
        RS.MultimeshSetCustomAabb(mm, new Aabb(new Vector3(-4096, -16384, -100), new Vector3(16384, 16384, 16384)));
        Rid instance = RS.InstanceCreate2(mm, scenario);
        RS.InstanceGeometrySetMaterialOverride(instance, material.GetRid());
        RS.InstanceGeometrySetCastShadowsSetting(instance, RS.ShadowCastingSetting.Off);
        return (mm, instance);
    }
}
