using System;
using System.Collections.Generic;
using Godot;

namespace Tzar2.Client.Viewer;

/// <summary>One tab of the asset viewer.</summary>
public interface IViewerPage : IDisposable
{
    /// <summary>Tab title.</summary>
    string Title { get; }

    /// <summary>The page's controls in the left panel.</summary>
    Control Panel { get; }

    /// <summary>Called when the page becomes (in)visible.</summary>
    void SetActive(bool active);

    /// <summary>Called every frame while active, after the clock advanced.</summary>
    void Update();
}

/// <summary>
/// The M1 asset viewer (TDD §25 M1, a dev mode of the client, DEC-11): sprites with all animations and directions in the
/// eight player colours with shadows and building overlays; terrain sets with transitions and water; fonts; sounds;
/// MIDI music. World content is drawn with the production renderer path (ADR 0010/0013) under a UI
/// <c>CanvasLayer</c>.
/// </summary>
public partial class AssetViewer : Node
{
    /// <summary>Width of the left panel in pixels.</summary>
    public const int PanelWidth = 340;

    private readonly ContentStore _store;
    private readonly ViewerSettings _settings;
    private readonly string? _soundFontArg;
    private readonly List<IViewerPage> _pages = [];
    private WorldView? _world;
    private TabBar? _tabs;
    private VBoxContainer? _pageHost;
    private SpinBox? _zoom;
    private CheckBox? _pause;
    private Label? _clock;
    private Vector2I _dragFrom;
    private bool _dragging;
    private int _active = -1;

    /// <summary>Creates the viewer over an opened package.</summary>
    public AssetViewer(ContentStore store, ViewerSettings settings, string? soundFontArg)
    {
        _store = store;
        _settings = settings;
        _soundFontArg = soundFontArg;
    }

    /// <summary>Parameterless constructor required by Godot for script instances; not used.</summary>
    public AssetViewer()
        : this(null!, null!, null)
    {
    }

    /// <summary>Raised when the user asks to open another package.</summary>
    public event Action? OpenPackageRequested;

    /// <summary>The package.</summary>
    public ContentStore Store => _store;

    /// <summary>The world view.</summary>
    public WorldView World => _world!;

    /// <summary>Viewer clock in ms (drives all animations).</summary>
    public long TimeMs { get; set; }

    /// <summary>Whether the clock is stopped.</summary>
    public bool Paused
    {
        get => _pause?.ButtonPressed ?? false;
        set
        {
            if (_pause is not null)
            {
                _pause.ButtonPressed = value;
            }
        }
    }

    /// <summary>Pages in tab order.</summary>
    public IReadOnlyList<IViewerPage> Pages => _pages;

    /// <summary>The sprite page.</summary>
    public SpritePage Sprites { get; private set; } = null!;

    /// <summary>The terrain page.</summary>
    public TerrainPage Terrain { get; private set; } = null!;

    /// <summary>The font page.</summary>
    public FontPage Fonts { get; private set; } = null!;

    /// <summary>The sound page.</summary>
    public SoundPage Sounds { get; private set; } = null!;

    /// <summary>The music page.</summary>
    public MusicPage Music { get; private set; } = null!;

    /// <inheritdoc/>
    public override void _Ready()
    {
        _world = new WorldView(GetViewport().FindWorld2D().Canvas)
        {
            Origin = new Vector2I(PanelWidth + 36, 48),
            BackgroundColour = Color.Color8(74, 92, 59),
        };
        _world.Resize(GetViewport().GetVisibleRect().Size is var s ? new Vector2I((int)s.X, (int)s.Y) : default);

        var ui = new CanvasLayer { Layer = 1 };
        AddChild(ui);
        var panel = new PanelContainer
        {
            Position = Vector2.Zero,
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            AnchorBottom = 1,
            OffsetRight = PanelWidth,
        };
        ui.AddChild(panel);
        var column = new VBoxContainer();
        panel.AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        string packageName = _store.Manifest is { } m ? $"{m.Name} {m.Version} ({m.SourceEdition})" : System.IO.Path.GetFileName(_store.Path);
        header.AddChild(new Label { Text = packageName, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = _store.Path });
        var open = new Button { Text = "Open…", TooltipText = "Choose another imported package (.t2pkg)" };
        open.Pressed += () => OpenPackageRequested?.Invoke();
        header.AddChild(open);

        _tabs = new TabBar { ClipTabs = false };
        column.AddChild(_tabs);
        _pageHost = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(_pageHost);

        var footer = new HBoxContainer();
        column.AddChild(footer);
        footer.AddChild(new Label { Text = "Zoom" });
        _zoom = new SpinBox { MinValue = 1, MaxValue = WorldView.MaxZoom, Step = 1, Value = 1, TooltipText = "Integer zoom (mouse wheel over the view); drag with the right/middle button or use the arrow keys to pan, Home resets." };
        _zoom.ValueChanged += v => _world.SetZoom((int)v);
        footer.AddChild(_zoom);
        _pause = new CheckBox { Text = "Pause" };
        footer.AddChild(_pause);
        _clock = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        footer.AddChild(_clock);

        Sprites = Add(new SpritePage(this));
        Terrain = Add(new TerrainPage(this));
        Fonts = Add(new FontPage(this));
        Sounds = Add(new SoundPage(this));
        Music = Add(new MusicPage(this, _settings, _soundFontArg));
        _tabs.TabChanged += tab => ShowPage((int)tab);
        ShowPage(0);
    }

    /// <summary>Switches to page <paramref name="index"/>.</summary>
    public void ShowPage(int index)
    {
        if (index == _active || index < 0 || index >= _pages.Count)
        {
            return;
        }

        if (_active >= 0)
        {
            _pages[_active].SetActive(false);
            _pageHost!.RemoveChild(_pages[_active].Panel);
        }

        _active = index;
        _pageHost!.AddChild(_pages[index].Panel);
        _pages[index].SetActive(true);
        if (_tabs!.CurrentTab != index)
        {
            _tabs.CurrentTab = index;
        }
    }

    /// <summary>Sets the zoom (also updates the spin box).</summary>
    public void SetZoom(int zoom)
    {
        _world!.SetZoom(zoom);
        _zoom!.SetValueNoSignal(_world.Zoom);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        _world!.Resize(new Vector2I((int)size.X, (int)size.Y));
        if (!Paused)
        {
            TimeMs += (long)Math.Round(delta * 1000);
        }

        _clock!.Text = $"t = {TimeMs / 1000}.{TimeMs % 1000:000} s";
        if (_active >= 0)
        {
            _pages[_active].Update();
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wheel when wheel.Position.X > PanelWidth:
                SetZoom(_world!.Zoom + 1);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wheel when wheel.Position.X > PanelWidth:
                SetZoom(_world!.Zoom - 1);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right or MouseButton.Middle } button:
                _dragging = button.Pressed;
                _dragFrom = (Vector2I)button.Position;
                break;
            case InputEventMouseMotion motion when _dragging:
                Vector2I now = (Vector2I)motion.Position;
                Vector2I moved = (_dragFrom - now) / _world!.Zoom;
                if (moved != Vector2I.Zero)
                {
                    _world.Scroll(moved);
                    _dragFrom -= moved * _world.Zoom;
                }

                break;
            case InputEventKey { Pressed: true } key:
                int step = key.ShiftPressed ? 128 : 32;
                switch (key.Keycode)
                {
                    case Key.Left:
                        _world!.Scroll(new Vector2I(-step, 0));
                        break;
                    case Key.Right:
                        _world!.Scroll(new Vector2I(step, 0));
                        break;
                    case Key.Up:
                        _world!.Scroll(new Vector2I(0, -step));
                        break;
                    case Key.Down:
                        _world!.Scroll(new Vector2I(0, step));
                        break;
                    case Key.Home:
                        _world!.ResetPan();
                        break;
                    case Key.Space:
                        Paused = !Paused;
                        break;
                }

                break;
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        foreach (IViewerPage page in _pages)
        {
            page.Dispose();

            // Inactive panels are not in the tree, so they are not freed with the viewer.
            if (!page.Panel.IsInsideTree())
            {
                page.Panel.Free();
            }
        }

        _pages.Clear();
        _world?.Dispose();
        _world = null;
    }

    private T Add<T>(T page)
        where T : IViewerPage
    {
        _pages.Add(page);
        _tabs!.AddTab(page.Title);
        return page;
    }
}
