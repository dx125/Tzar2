using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Content.Rules;
using Tzar2.Tools.Cli.Fidelity.Yaml;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>
/// The committed capture facts (tests/fidelity/facts, ADR 0111) against the values the rebuild uses. When a later
/// session changes a fact, these fail until the engine follows (or the fact is explained in an ADR).
/// </summary>
public class FidelityFactsTests
{
    private static YamlMapping Analysis(string scenario, string analysis)
    {
        string path = Path.Combine(OriginPaths.RepositoryRoot, "tests", "fidelity", "facts", scenario + ".yaml");
        var doc = (YamlMapping)MiniYaml.Parse(File.ReadAllText(path));
        Assert.Equal(FidelityAnalyzer.FactsSchema, ((YamlScalar)doc.Get("schema")!).Value);
        Assert.Equal("true", ((YamlScalar)((YamlMapping)doc.Get("provenance")!).Get("install_matches_reference")!).Value);
        return (YamlMapping)((YamlMapping)doc.Get("analyses")!).Get(analysis)!;
    }

    private static string Fact(YamlMapping analysis, string name) => ((YamlScalar)((YamlMapping)analysis.Get("facts")!).Get(name)!).Value;

    [Fact]
    public void Shadow_factor_of_the_client_matches_the_measured_halving()
    {
        YamlMapping shadow = Analysis("shadow-blend", "shadow-blend");
        Assert.Equal("ok", ((YamlScalar)shadow.Get("status")!).Value);
        Assert.Equal("floor(c*16/32)", Fact(shadow, "model"));

        string shader = File.ReadAllText(Path.Combine(OriginPaths.RepositoryRoot, "src", "Tzar2.Client", "shaders", "shadow_composite.gdshader"));
        Match m = Regex.Match(shader, @"uniform float strength = ([0-9.]+);");
        Assert.True(m.Success);
        Assert.Equal(0.5, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Kingdom_n_uses_ramp_n_as_the_engine_assumes()
    {
        YamlMapping colours = Analysis("player-colours", "player-colours");
        Assert.Equal("ok", ((YamlScalar)colours.Get("status")!).Value);
        Assert.Equal("true", Fact(colours, "swatch_identity"));
        Assert.True(int.Parse(Fact(colours, "swatches_decided"), CultureInfo.InvariantCulture) >= 7);
    }

    [Fact]
    public void The_game_surface_is_rgb555()
    {
        YamlMapping depth = Analysis("colour-depth", "colour-depth");
        var trials = ((YamlSequence)((YamlMapping)depth.Get("facts")!).Get("trials")!).Items.Cast<YamlMapping>();
        Assert.All(trials, t => Assert.Equal("rgb555", ((YamlScalar)t.Get("game_surface")!).Value));
    }
}
