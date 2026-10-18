using System;
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
/// </summary>
public sealed class SkirmishAi
{
    /// <summary>Folder of the original AI scripts in a base package.</summary>
    public const string PackageFolder = "text/data/ai/";

    /// <summary>Path of the engine-default script in the engine defaults.</summary>
    public const string EngineDefaultPath = "ai/skirmish.ais";

    private static readonly Lazy<SkirmishAi> LazyDefault = new(() => new SkirmishAi(
        AiCompiler.CompileText("skirmish.ais", Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes(EngineDefaultPath))),
        isOriginal: false,
        notice: null));

    private SkirmishAi(AiProgram program, bool isOriginal, string? notice)
    {
        Program = program;
        IsOriginal = isOriginal;
        Notice = notice;
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

        return new SkirmishAi(original, true, null);
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
