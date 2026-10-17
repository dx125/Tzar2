global using Tzar2.Content.Rules;
using System.Globalization;

namespace Tzar2.Tools.Cli.Fidelity.Yaml;

/// <summary>
/// The capture scenarios' YAML reader (ADR 0030): the rules parser of Tzar2.Content (<see cref="RulesYaml"/>, ADR 0031)
/// plus a floating-point accessor, which the deterministic Content assembly does not offer.
/// </summary>
internal static class MiniYaml
{
    public static YamlNode Parse(string text) => RulesYaml.Parse(text);

    public static string AsString(YamlNode node, string what) => RulesYaml.AsString(node, what);

    public static int AsInt(YamlNode node, string what) => RulesYaml.AsInt(node, what);

    public static bool AsBool(YamlNode node, string what) => RulesYaml.AsBool(node, what);

    public static YamlMapping AsMapping(YamlNode node, string what) => RulesYaml.AsMapping(node, what);

    public static YamlSequence AsSequence(YamlNode node, string what) => RulesYaml.AsSequence(node, what);

    public static double AsDouble(YamlNode node, string what)
    {
        string text = AsString(node, what);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : throw new YamlException($"{what}: '{text}' is not a number", node.Line);
    }
}
