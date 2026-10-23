using System;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Content.MapGen;

namespace Tzar2.Client.Editor;

/// <summary>The editor's dialogs for map-level data (TDD §18.2: map properties, kingdoms, user resources, sounds and music, new map).</summary>
public static class Dialogs
{
    private static readonly string[] KingdomNames = Enumerable.Range(1, 8).Select(k => "Kingdom " + k.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Map properties: name, author, description; game type, victory, population limit, difficulty, No Fog, Explore.</summary>
    public static void MapProperties(Node host, MapDocument doc)
    {
        Forms.Dialog(host, "Map properties", new Vector2I(520, 560), body =>
        {
            MapProperties p = doc.State.Scenario.Properties;
            void Set(string label, Func<MapProperties, MapProperties> change) => doc.Apply(label, s => s.WithScenario(m => m with { Properties = change(m.Properties) }));
            GridContainer g = Forms.Grid(body);
            Forms.Text(g, "Name", p.Title, v => Set("Map name", x => x with { Title = v }));
            Forms.Text(g, "Author", p.Author, v => Set("Author", x => x with { Author = v }));
            Forms.Multiline(g, "Description", p.Description, v => Set("Description", x => x with { Description = v }), 120);
            Forms.Enum(g, "Game type", p.GameType, v => Set("Game type", x => x with { GameType = v }));
            Forms.Enum(g, "Victory condition", p.Victory, v => Set("Victory condition", x => x with { Victory = v }));
            Forms.Number(g, "Population limit (0 = default)", p.PopulationLimit, 0, 2000, v => Set("Population limit", x => x with { PopulationLimit = v }));
            Forms.Choice(g, "Difficulty", ["Easy", "Normal", "Hard"], p.Difficulty, v => Set("Difficulty", x => x with { Difficulty = v }));
            Forms.Check(g, "No Fog", p.NoFog, v => Set("No Fog", x => x with { NoFog = v }));
            Forms.Check(g, "Explore", p.Explore, v => Set("Explore", x => x with { Explore = v }));
            Forms.Text(g, "Source language", doc.State.Extras.Language, v => doc.Apply("Language", s => s with { Extras = s.Extras with { Language = v } }));
        });
    }

    /// <summary>Kingdoms: general, resources (fill and equalize), AI, technology, diplomacy (Appendix C.1).</summary>
    public static void Kingdoms(Node host, MapDocument doc, Ruleset ruleset)
    {
        Forms.Dialog(host, "Kingdoms", new Vector2I(760, 680), body =>
        {
            var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(700, 560) };
            body.AddChild(tabs);
            for (int number = 1; number <= KingdomEdits.Kingdoms; number++)
            {
                int k = number;
                VBoxContainer page = Forms.Scroll(tabs, "K" + k.ToString(CultureInfo.InvariantCulture));
                Kingdom(page, doc, ruleset, k);
            }

            VBoxContainer diplomacy = Forms.Scroll(tabs, "Diplomacy");
            Diplomacy(diplomacy, doc);
        });
    }

    private static void Kingdom(VBoxContainer page, MapDocument doc, Ruleset ruleset, int k)
    {
        KingdomDef d = doc.State.Scenario.KingdomOrDefault(k);
        void Set(string label, Func<KingdomDef, KingdomDef> change) => doc.Apply($"{label} (kingdom {k})", s => s.WithScenario(m => m.UpdateKingdom(k, change)));
        Forms.Heading(page, "General");
        GridContainer g = Forms.Grid(page);
        Forms.Text(g, "Kingdom name", d.Name, v => Set("Kingdom name", x => x with { Name = v }));
        Forms.Text(g, "Tzar", d.TzarName, v => Set("Tzar name", x => x with { TzarName = v }));
        Forms.Enum(g, "Civilization", d.Civilization, v => Set("Civilization", x => x with { Civilization = v }));
        Forms.Enum(g, "Player type", d.PlayerType, v => Set("Player type", x => x with { PlayerType = v }));
        Forms.Check(g, "Active", d.Active, v => Set("Active", x => x with { Active = v }));
        Forms.Check(g, "Shared victory", d.SharedVictory, v => Set("Shared victory", x => x with { SharedVictory = v }));
        Forms.Label(g, "Starting location");
        Forms.Label(g, d.StartTileX >= 0 ? $"({d.StartTileX}, {d.StartTileY})" : "not set (Starts tab or keys 1–8)");

        Forms.Heading(page, "Resources");
        GridContainer r = Forms.Grid(page);
        foreach (KingdomResource res in Enum.GetValues<KingdomResource>())
        {
            KingdomResource each = res;
            Forms.Number(r, res.ToString(), d.Get(res), 0, 10_000_000, v => Set(each.ToString(), x => x.With(each, v)));
        }

        var fills = new HBoxContainer();
        for (int level = 0; level < ruleset.Gameplay.StartingResources.Length; level++)
        {
            int l = level;
            Forms.Button(fills, "Fill " + ruleset.Gameplay.StartingResources[level].Key, () => doc.Apply("Fill resources", s => s.WithScenario(m => m.Fill(ruleset, k, l))));
        }

        Forms.Button(fills, "Equalize column (gold)", () => doc.Apply("Equalize column", s => s.WithScenario(m => m.EqualizeColumn(KingdomResource.Gold, k))));
        Forms.Button(fills, "Equalize row", () => doc.Apply("Equalize row", s => s.WithScenario(m => m.EqualizeRow(k, KingdomResource.Gold))));
        Forms.Button(fills, "Equalize all", () => doc.Apply("Equalize all", s => s.WithScenario(m => m.EqualizeAll(k, KingdomResource.Gold))));
        page.AddChild(fills);

        Forms.Heading(page, "AI");
        GridContainer a = Forms.Grid(page);
        Forms.Text(a, "AI script", d.AiScript, v => Set("AI script", x => x with { AiScript = v }));
        Forms.Check(a, "Economy AI", d.EconomyAi, v => Set("Economy AI", x => x with { EconomyAi = v }));
        Forms.Check(a, "War AI", d.WarAi, v => Set("War AI", x => x with { WarAi = v }));

        Forms.Heading(page, "Technology");
        KeyList(page, "Prohibited buildings", KingdomEdits.BuildingKeys(ruleset), d.ForbiddenBuildings, (key, on) => Set("Prohibited building", x => x with { ForbiddenBuildings = KingdomEdits.Toggle(x.ForbiddenBuildings, key, on) }));
        KeyList(page, "Prohibited units", KingdomEdits.UnitKeys(ruleset), d.ForbiddenUnits, (key, on) => Set("Prohibited unit", x => x with { ForbiddenUnits = KingdomEdits.Toggle(x.ForbiddenUnits, key, on) }));
        KeyList(page, "Prohibited researches", KingdomEdits.ResearchKeys(ruleset), d.ForbiddenResearches, (key, on) => Set("Prohibited research", x => x with { ForbiddenResearches = KingdomEdits.Toggle(x.ForbiddenResearches, key, on) }));
        KeyList(page, "Prohibited upgrades", KingdomEdits.UpgradeKeys(ruleset), d.ForbiddenUpgrades, (key, on) => Set("Prohibited upgrade", x => x with { ForbiddenUpgrades = KingdomEdits.Toggle(x.ForbiddenUpgrades, key, on) }));
        KeyList(page, "Initial researches", KingdomEdits.ResearchKeys(ruleset), d.InitialResearches, (key, on) => Set("Initial research", x => x with { InitialResearches = KingdomEdits.Toggle(x.InitialResearches, key, on) }));
    }

    private static void KeyList(VBoxContainer page, string title, System.Collections.Generic.IReadOnlyList<string> keys, EquatableArray<string> chosen, Action<string, bool> toggle)
    {
        var fold = new FoldableContainer { Title = $"{title} ({chosen.Length})", Folded = true };
        var grid = new GridContainer { Columns = 3 };
        foreach (string key in keys)
        {
            string each = key;
            var box = new CheckBox { Text = key, ButtonPressed = chosen.Items.Contains(key) };
            box.Toggled += on => toggle(each, on);
            grid.AddChild(box);
        }

        fold.AddChild(grid);
        page.AddChild(fold);
    }

    private static void Diplomacy(VBoxContainer page, MapDocument doc)
    {
        Forms.Label(page, "Row kingdom → column kingdom. Allies may share vision, resources and unit control.");
        var grid = new GridContainer { Columns = 9 };
        Forms.Label(grid, string.Empty);
        foreach (string n in KingdomNames)
        {
            Forms.Label(grid, n.Replace("Kingdom ", "K", StringComparison.Ordinal));
        }

        for (int from = 1; from <= 8; from++)
        {
            Forms.Label(grid, "K" + from.ToString(CultureInfo.InvariantCulture));
            for (int to = 1; to <= 8; to++)
            {
                if (from == to)
                {
                    Forms.Label(grid, "—");
                    continue;
                }

                int f = from, t = to;
                DiplomacyDef d = doc.State.Scenario.DiplomacyOrDefault(f, t);
                var cell = new VBoxContainer();
                var stance = new OptionButton();
                foreach (Stance s in Enum.GetValues<Stance>())
                {
                    stance.AddItem(s.ToString());
                }

                stance.Selected = (int)d.Stance;
                stance.ItemSelected += i => doc.Apply("Diplomacy", s => s.WithScenario(m => m.SetDiplomacy(m.DiplomacyOrDefault(f, t) with { Stance = (Stance)(int)i }, both: false)));
                cell.AddChild(stance);
                var flags = new HBoxContainer();
                Forms.Check(flags, "V", d.SharedVision, v => doc.Apply("Shared vision", s => s.WithScenario(m => m.SetDiplomacy(m.DiplomacyOrDefault(f, t) with { SharedVision = v }, both: false))), inGrid: false).TooltipText = "Shared vision";
                Forms.Check(flags, "R", d.SharedResources, v => doc.Apply("Shared resources", s => s.WithScenario(m => m.SetDiplomacy(m.DiplomacyOrDefault(f, t) with { SharedResources = v }, both: false))), inGrid: false).TooltipText = "Shared resources";
                Forms.Check(flags, "U", d.CommandUnits, v => doc.Apply("Unit control", s => s.WithScenario(m => m.SetDiplomacy(m.DiplomacyOrDefault(f, t) with { CommandUnits = v }, both: false))), inGrid: false).TooltipText = "Command units";
                cell.AddChild(flags);
                grid.AddChild(cell);
            }
        }

        page.AddChild(grid);
    }

    /// <summary>User resources: import, export and delete images, sounds and music; the custom mini-map and title image.</summary>
    public static void Resources(Node host, MapDocument doc, Func<string, byte[]?> convertImage)
    {
        Forms.Dialog(host, "User resources", new Vector2I(620, 560), body =>
        {
            var list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(560, 300) };
            void Fill()
            {
                list.Clear();
                foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Immutable.ImmutableArray<byte>> a in doc.State.Assets)
                {
                    list.AddItem(string.Create(CultureInfo.InvariantCulture, $"{a.Key}  ({a.Value.Length:N0} bytes, {AssetEdits.KindOf(a.Key)})"));
                }
            }

            body.AddChild(list);
            var row = new HBoxContainer();
            Forms.Button(row, "Import…", () => Files.Open(host, ["*.bmp ; BMP", "*.png ; PNG", "*.wav ; WAV", "*.ogg ; OGG", "*.mid, *.midi ; MIDI"], paths =>
            {
                doc.Apply("Import resources", s =>
                {
                    foreach (string path in paths)
                    {
                        byte[] data = System.IO.File.ReadAllBytes(path);
                        string target = AssetEdits.PathFor(path);
                        if (target.EndsWith(".bmp", StringComparison.Ordinal) && convertImage(path) is { } png)
                        {
                            data = png;
                            target = target[..^4] + ".png";
                        }

                        s = s.Import(target, data);
                    }

                    return s;
                });
                Fill();
            }));
            Forms.Button(row, "Set as mini-map…", () => Files.Open(host, ["*.png, *.bmp ; Images"], paths =>
            {
                byte[] data = paths[0].EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? convertImage(paths[0]) ?? [] : System.IO.File.ReadAllBytes(paths[0]);
                doc.Apply("Custom mini-map", s => s.Import(AssetEdits.MinimapPath, data));
                Fill();
            }, many: false));
            Forms.Button(row, "Set as title image…", () => Files.Open(host, ["*.png, *.bmp ; Images"], paths =>
            {
                byte[] data = paths[0].EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? convertImage(paths[0]) ?? [] : System.IO.File.ReadAllBytes(paths[0]);
                doc.Apply("Custom title image", s => s.Import(AssetEdits.TitlePath, data));
                Fill();
            }, many: false));
            Forms.Button(row, "Export…", () =>
            {
                int[] sel = list.GetSelectedItems();
                if (sel.Length == 0)
                {
                    return;
                }

                string key = doc.State.Assets.Keys.ElementAt(sel[0]);
                Files.Save(host, System.IO.Path.GetFileName(key), path => System.IO.File.WriteAllBytes(path, doc.State.Export(key)!));
            });
            Forms.Button(row, "Delete", () =>
            {
                int[] sel = list.GetSelectedItems();
                if (sel.Length > 0)
                {
                    string key = doc.State.Assets.Keys.ElementAt(sel[0]);
                    doc.Apply("Delete resource", s => s.DeleteAsset(key));
                    Fill();
                }
            });
            body.AddChild(row);
            Fill();
        });
    }

    /// <summary>Custom sounds (point or object source; Every / Looped / Random from–to / Delayed start) and the music playlist.</summary>
    public static void Sounds(Node host, MapDocument doc, Func<int> selectedObject)
    {
        Forms.Dialog(host, "Custom sounds and music", new Vector2I(640, 620), body =>
        {
            void Rebuild()
            {
                Forms.Clear(body);
                ScenarioModel m = doc.State.Scenario;
                for (int i = 0; i < m.Sounds.Length; i++)
                {
                    int index = i;
                    CustomSoundDef s = m.Sounds[i];
                    void Set(Func<CustomSoundDef, CustomSoundDef> change) => doc.Apply("Custom sound", st => st.WithScenario(x => x.SetSound(index, change(x.Sounds[index]))));
                    Forms.Heading(body, s.Name);
                    GridContainer g = Forms.Grid(body);
                    Forms.Text(g, "Name", s.Name, v => Set(x => x with { Name = v }));
                    Forms.Text(g, "Sound", s.Sound.Path, v => Set(x => x with { Sound = new ResourceRef(v) }));
                    Forms.Enum(g, "Repeat", s.Repeat, v => Set(x => x with { Repeat = v }));
                    Forms.Number(g, "Interval / min / delay (ms)", s.IntervalMs, 0, 3_600_000, v => Set(x => x with { IntervalMs = v }));
                    Forms.Number(g, "Max interval (ms, Random)", s.IntervalMaxMs, 0, 3_600_000, v => Set(x => x with { IntervalMaxMs = v }));
                    Forms.Label(g, "Source");
                    Forms.Label(g, s.SourceObject is int o ? $"object #{o}" : $"point ({s.X}, {s.Y})");
                    Forms.Number(g, "Point X", s.X, 0, 1024, v => Set(x => x with { X = v }));
                    Forms.Number(g, "Point Y", s.Y, 0, 1024, v => Set(x => x with { Y = v }));
                    var row = new HBoxContainer();
                    Forms.Button(row, "Follow selected object", () => { Set(x => x with { SourceObject = selectedObject() is > 0 and var id ? id : null }); Rebuild(); });
                    Forms.Button(row, "Point source", () => { Set(x => x with { SourceObject = null }); Rebuild(); });
                    Forms.Button(row, "Remove", () => { doc.Apply("Remove sound", st => st.WithScenario(x => x.RemoveSound(index))); Rebuild(); });
                    body.AddChild(row);
                }

                Forms.Button(body, "Add sound", () =>
                {
                    doc.Apply("Add sound", st => st.WithScenario(x => x.AddSound(new CustomSoundDef(x.FreshName("sound"), new ResourceRef("user/sound.wav"), SoundRepeat.Every, 10_000, 0, null, 0, 0), out _)));
                    Rebuild();
                });

                Forms.Heading(body, "Music playlist (played in random order)");
                string playlist = string.Join("\n", m.Music.Items.Select(r => r.Path));
                Forms.Multiline(body, "One resource path per line", playlist, v =>
                    doc.Apply("Music playlist", st => st.WithScenario(x => x.WithMusic(v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => new ResourceRef(p))))), 120);
            }

            Rebuild();
        });
    }

    /// <summary>New map: empty (size, terrain set, start terrain, patches, rocks, animals) or random (generator options).</summary>
    public static void NewMap(Node host, Ruleset ruleset, string[] sets, Action<MapDocument> created, Func<string?, TerrainRules> rules)
    {
        Forms.Dialog(host, "New map", new Vector2I(520, 600), body =>
        {
            string name = "New map";
            int size = 2;
            int set = 0;
            int patches = 2, rocks = 2, animals = 6;
            Forms.Heading(body, "Empty map");
            GridContainer g = Forms.Grid(body);
            Forms.Text(g, "Name", name, v => name = v);
            int[] sizes = [.. MapDocument.ClassicSizes, 768, 1024];
            Forms.Choice(g, "Size", sizes.Select(s => $"{s} × {s}" + (s > 512 ? " (extended)" : string.Empty)).ToArray(), size, i => size = i);
            Forms.Choice(g, "Terrain set", sets, 0, i => set = i);
            Forms.Number(g, "Ground patches per 100 tiles", patches, 0, 20, v => patches = v);
            Forms.Number(g, "Rocks per kingdom", rocks, 0, 8, v => rocks = v);
            Forms.Number(g, "Animals", animals, 0, 64, v => animals = v);
            Forms.Button(body, "Create empty map", () =>
            {
                string setId = sets[set];
                created(Content.Editing.NewMap.Empty(new NewMapOptions(name, sizes[size], setId, null, patches, rocks, animals, (ulong)Time.GetTicksUsec()), rules(setId), ruleset));
            });

            Forms.Heading(body, "Random map");
            var options = new MapGenOptions();
            GridContainer r = Forms.Grid(body);
            Forms.Enum(r, "Type", options.Type, v => options = options with { Type = v });
            Forms.Enum(r, "Size", options.Size, v => options = options with { Size = v });
            Forms.Enum(r, "Terrain", options.TerrainSet, v => options = options with { TerrainSet = v });
            Forms.Number(r, "Players", options.Players, 1, 8, v => options = options with { Players = v });
            Forms.Enum(r, "Resources", options.Resources, v => options = options with { Resources = v });
            foreach (MapFeatures feature in Enum.GetValues<MapFeatures>().Where(f => f != 0 && (f & (f - 1)) == 0))
            {
                MapFeatures each = feature;
                Forms.Check(r, feature.ToString(), (options.Features & feature) != 0, v => options = options with { Features = v ? options.Features | each : options.Features & ~each });
            }

            Forms.Button(body, "Generate random map", () =>
            {
                MapGenOptions bound = (options with { Seed = (ulong)Time.GetTicksUsec() }).BindTo(ruleset);
                try
                {
                    created(Content.Editing.NewMap.Random(bound, rules(RandomMapNames.SetId(bound.TerrainSet)), name));
                }
                catch (MapGenException e)
                {
                    GD.PrintErr("Random map: " + e.Message);
                }
            });
        });
    }
}

/// <summary>Native file dialogs for the editor (any folder on disk, REQ-EDT-04).</summary>
public static class Files
{
    /// <summary>Asks for one or several existing files.</summary>
    public static void Open(Node host, string[] filters, Action<string[]> chosen, bool many = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        var dialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = many ? FileDialog.FileModeEnum.OpenFiles : FileDialog.FileModeEnum.OpenFile, Filters = filters, UseNativeDialog = true };
        dialog.FilesSelected += paths => chosen(paths);
        dialog.FileSelected += path => chosen([path]);
        host.AddChild(dialog);
        dialog.Size = new Vector2I(800, 560);
        Forms.ShowCentered(dialog);
    }

    /// <summary>Asks for a folder.</summary>
    public static void Folder(Node host, Action<string> chosen)
    {
        ArgumentNullException.ThrowIfNull(host);
        var dialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenDir, UseNativeDialog = true };
        dialog.DirSelected += path => chosen(path);
        host.AddChild(dialog);
        dialog.Size = new Vector2I(800, 560);
        Forms.ShowCentered(dialog);
    }

    /// <summary>Asks for a file to write.</summary>
    public static void Save(Node host, string suggested, Action<string> chosen, string[]? filters = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var dialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.SaveFile, CurrentFile = suggested, Filters = filters ?? [], UseNativeDialog = true };
        dialog.FileSelected += path => chosen(path);
        host.AddChild(dialog);
        dialog.Size = new Vector2I(800, 560);
        Forms.ShowCentered(dialog);
    }
}
