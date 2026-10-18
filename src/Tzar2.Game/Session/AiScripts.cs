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
/// The strategy script computer kingdoms run (ADR 0052, 0073): the original <c>random.ais</c> from the user's imported
/// base package (<c>text/data/ai/*</c>, with its includes), and the engine-default <c>content/engine/ai/skirmish.ais</c>
/// when the package has no scripts or the original does not compile. No I/O of its own: the package is passed in.
/// </summary>
public static class AiScripts
{
    /// <summary>Folder of the original AI scripts in a base package.</summary>
    public const string PackageFolder = "text/data/ai/";

    /// <summary>Path of the engine-default script in the engine defaults.</summary>
    public const string EngineDefault = "ai/skirmish.ais";

    /// <summary>The engine-default script, compiled.</summary>
    public static AiProgram Default()
        => AiCompiler.CompileText("skirmish.ais", Encoding.UTF8.GetString(new EngineDefaultsSource().ReadAllBytes(EngineDefault)));

    /// <summary>
    /// The skirmish AI of <paramref name="package"/>: <see cref="AiIni.DefaultScript"/> compiled from its scripts, or
    /// <see cref="Default"/> when it has none or the compile reports an error.
    /// </summary>
    /// <param name="package">The imported base package (null = engine default).</param>
    /// <param name="usedOriginal">Whether the original script was used.</param>
    public static AiProgram ForSkirmish(IContentSource? package, out bool usedOriginal)
    {
        usedOriginal = false;
        if (package is not null)
        {
            var source = new PackageScripts(package);
            if (source.TryRead(AiIni.DefaultScript, out _))
            {
                AiProgram original = AiCompiler.Compile(AiIni.DefaultScript, source);
                if (!original.HasErrors)
                {
                    usedOriginal = true;
                    return original;
                }
            }
        }

        return Default();
    }

    /// <summary>The AI difficulty of a lobby difficulty.</summary>
    public static AiDifficulty ToAi(Difficulty difficulty) => difficulty switch
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
