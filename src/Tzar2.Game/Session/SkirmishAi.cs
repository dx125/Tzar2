using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Runtime;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;

namespace Tzar2.Game.Session;

/// <summary>
/// The strategy script the computer kingdoms of a match run (ADR 0052, 0073), resolved behind the Game layer so the
/// client never sees AI types (§7.2): the original <c>random.ais</c> from the user's imported base package
/// (<c>text/data/ai/*</c>, with its includes), else the engine-default <c>content/engine/ai/skirmish.ais</c>. Its
/// <see cref="ContentHash"/> is part of the match fingerprint and the replay header: peers must resolve the same program.
/// The other strategies the package's <c>AI.INI</c> offers (<see cref="Scripts"/>) are chosen per player in the lobby
/// (AI-001, SHELL-016, SHELL-017) and resolved with <see cref="ForScript"/>.
/// </summary>
public sealed class SkirmishAi
{
    /// <summary>Path of <c>AI.INI</c> in a base package (imported as an INI table, docs/formats/ini-tsv.md).</summary>
    public const string AiIniPath = "tables/data/ai/ai.ini.json";

    /// <summary>Folder of the original AI scripts in a base package.</summary>
    public const string PackageFolder = "text/data/ai/";

    /// <summary>Path of the engine-default script in the engine defaults.</summary>
    public const string EngineDefaultPath = "ai/skirmish.ais";

    private static readonly Lazy<SkirmishAi> LazyDefault = new(() => new SkirmishAi(
        AiCompiler.CompileText("skirmish.ais", Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes(EngineDefaultPath))),
        isOriginal: false,
        notice: null));

    private readonly IAiScriptSource? _source;
    private readonly Dictionary<string, SkirmishAi> _byScript = new(StringComparer.Ordinal);

    private SkirmishAi(AiProgram program, bool isOriginal, string? notice, IAiScriptSource? source = null, IReadOnlyList<AiScriptOption>? scripts = null)
    {
        Program = program;
        IsOriginal = isOriginal;
        Notice = notice;
        _source = source;
        Scripts = scripts ?? [new AiScriptOption(isOriginal ? "Random" : "Default", string.Empty)];
    }

    /// <summary>
    /// The strategies a player can choose (AI-001): first this one (script <c>""</c>), then the package's
    /// <c>AI.INI</c> entries. The engine default offers only itself.
    /// </summary>
    public IReadOnlyList<AiScriptOption> Scripts { get; }

    /// <summary>
    /// The AI of <paramref name="script"/> (a <see cref="AiScriptOption.Script"/>): this one for <c>""</c>, an unknown
    /// script or one that does not compile. Peers that resolve differently fail the loading barrier (AI hashes).
    /// </summary>
    public SkirmishAi ForScript(string? script)
    {
        if (string.IsNullOrEmpty(script) || _source is null)
        {
            return this;
        }

        lock (_byScript)
        {
            if (_byScript.TryGetValue(script, out SkirmishAi? known))
            {
                return known;
            }

            SkirmishAi resolved = this;
            foreach (AiScriptOption option in Scripts)
            {
                if (option.Script == script)
                {
                    AiProgram program = AiCompiler.Compile(script, _source);
                    resolved = program.HasErrors ? this : new SkirmishAi(program, IsOriginal, null);
                    break;
                }
            }

            _byScript[script] = resolved;
            return resolved;
        }
    }

    /// <summary>The engine-default script (public CI, packages without scripts).</summary>
    public static SkirmishAi EngineDefault => LazyDefault.Value;

    /// <summary>Whether the original <c>random.ais</c> of the package is used.</summary>
    public bool IsOriginal { get; }

    /// <summary>Hash of the compiled program (match fingerprint, replay header, AI snapshot state).</summary>
    public ulong ContentHash => Program.ContentHash;

    /// <summary>The root script name.</summary>
    public string Script => Program.Root;

    /// <summary>
    /// Why the engine default is used although a package was given (no original scripts, or they did not compile), for
    /// the menus; null when the original is used or no package was given.
    /// </summary>
    public string? Notice { get; }

    internal AiProgram Program { get; }

    /// <summary>
    /// Resolves the skirmish AI of <paramref name="package"/>: <see cref="AiIni.DefaultScript"/> compiled from its
    /// scripts, else <see cref="EngineDefault"/> with a <see cref="Notice"/>.
    /// </summary>
    public static SkirmishAi Resolve(IContentSource? package)
    {
        if (package is null)
        {
            return EngineDefault;
        }

        var source = new PackageScripts(package);
        if (!source.TryRead(AiIni.DefaultScript, out _))
        {
            return new SkirmishAi(EngineDefault.Program, false, $"The original AI scripts ({AiIni.DefaultScript}) are not in the package: computer kingdoms use Tzar2's default AI.");
        }

        AiProgram original = AiCompiler.Compile(AiIni.DefaultScript, source);
        if (original.HasErrors)
        {
            string first = string.Empty;
            foreach (var d in original.Diagnostics)
            {
                if (d.Severity == AI.Legacy.Syntax.AiDiagnosticSeverity.Error)
                {
                    first = d.ToString();
                    break;
                }
            }

            return new SkirmishAi(EngineDefault.Program, false, $"The original AI scripts do not compile ({first}): computer kingdoms use Tzar2's default AI.");
        }

        var scripts = new List<AiScriptOption> { new("Random", string.Empty) };
        foreach ((string name, string script) in ScriptsOfAiIni(package))
        {
            if (script != AiIni.DefaultScript && source.TryRead(script, out _) && scripts.TrueForAll(o => o.Script != script))
            {
                scripts.Add(new AiScriptOption(name, script));
            }
        }

        return new SkirmishAi(original, true, null, source, scripts);
    }

    // AI.INI [scripts] (Appendix D.1) as the importer stores it: sections with [key, value] entries; comments are gone.
    private static List<(string Name, string Script)> ScriptsOfAiIni(IContentSource package)
    {
        var result = new List<(string, string)>();
        if (!package.Contains(AiIniPath))
        {
            return result;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(package.ReadAllBytes(AiIniPath));
            foreach (System.Text.Json.JsonElement section in doc.RootElement.GetProperty("sections").EnumerateArray())
            {
                if (!string.Equals(section.GetProperty("name").GetString()?.Trim(), "scripts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (System.Text.Json.JsonElement entry in section.GetProperty("entries").EnumerateArray())
                {
                    string? key = entry[0].GetString()?.Trim();
                    string? value = entry[1].GetString()?.Trim();
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        result.Add((key, AiScriptNames.Normalize(value)));
                    }
                }
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            result.Clear(); // a malformed table offers no choices; the default still plays
        }

        return result;
    }

    /// <summary>The AI difficulty of a lobby difficulty.</summary>
    internal static AiDifficulty ToAi(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => AiDifficulty.Easy,
        Difficulty.Hard => AiDifficulty.Hard,
        _ => AiDifficulty.Normal,
    };

    // The package's AI folder as a script source (names normalised; the importer stores texts as UTF-8).
    private sealed class PackageScripts(IContentSource package) : IAiScriptSource
    {
        public bool TryRead(string name, [NotNullWhen(true)] out string? text)
        {
            string normalized = AiScriptNames.Normalize(name);
            string path = PackageFolder + normalized;
            if (!AiScriptNames.IsSafe(normalized) || !package.Contains(path))
            {
                text = null;
                return false;
            }

            text = Encoding.UTF8.GetString(package.ReadAllBytes(path));
            return true;
        }
    }
}

/// <summary>A strategy a player can choose for his kingdom's computer player (AI-001; <c>AI.INI</c> <c>[scripts]</c>).</summary>
/// <param name="Name">The name shown, e.g. <c>Magic, Religion</c>.</param>
/// <param name="Script">The script, or <c>""</c> for the match's default.</param>
public sealed record AiScriptOption(string Name, string Script);
