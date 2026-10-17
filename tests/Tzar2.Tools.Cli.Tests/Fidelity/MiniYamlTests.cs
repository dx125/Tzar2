using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Rules;
using Tzar2.Tools.Cli.Fidelity.Yaml;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

public class MiniYamlTests
{
    private static YamlMapping Map(string text) => Assert.IsType<YamlMapping>(MiniYaml.Parse(text));

    private static string S(YamlNode? node) => Assert.IsType<YamlScalar>(node).Value;

    [Fact]
    public void Parses_nested_mappings_sequences_and_scalars()
    {
        YamlMapping root = Map("""
            # comment
            id: smoke   # trailing comment
            title: "A: quoted # not a comment"
            single: 'it''s'
            nested:
              a: 1
              b:
                c: x
            list:
              - one
              - key: v
                other: w
              -
                deep: 1
            seq_at_same_indent:
            - p
            - q
            empty:
            """);
        Assert.Equal("smoke", S(root.Get("id")));
        Assert.Equal("A: quoted # not a comment", S(root.Get("title")));
        Assert.Equal("it's", S(root.Get("single")));
        var nested = Assert.IsType<YamlMapping>(root.Get("nested"));
        Assert.Equal("1", S(nested.Get("a")));
        Assert.Equal("x", S(Assert.IsType<YamlMapping>(nested.Get("b")).Get("c")));
        var list = Assert.IsType<YamlSequence>(root.Get("list"));
        Assert.Equal(3, list.Items.Count);
        Assert.Equal("one", S(list.Items[0]));
        var item = Assert.IsType<YamlMapping>(list.Items[1]);
        Assert.Equal(["key", "other"], item.Entries.Select(e => e.Key));
        Assert.Equal("1", S(Assert.IsType<YamlMapping>(list.Items[2]).Get("deep")));
        Assert.Equal(["p", "q"], Assert.IsType<YamlSequence>(root.Get("seq_at_same_indent")).Items.Select(S));
        Assert.True(Assert.IsType<YamlScalar>(root.Get("empty")).IsNull);
    }

    [Fact]
    public void Parses_flow_collections_and_escapes()
    {
        YamlMapping root = Map("""
            a: [1, -map, "${x}", 'y z', []]
            b: {k: v, n: [1, 2], e: "", q: "a\"b\\c\n"}
            """);
        var a = Assert.IsType<YamlSequence>(root.Get("a"));
        Assert.Equal(["1", "-map", "${x}", "y z"], a.Items.Take(4).Select(S));
        Assert.Empty(Assert.IsType<YamlSequence>(a.Items[4]).Items);
        var b = Assert.IsType<YamlMapping>(root.Get("b"));
        Assert.Equal("v", S(b.Get("k")));
        Assert.Equal(["1", "2"], Assert.IsType<YamlSequence>(b.Get("n")).Items.Select(S));
        Assert.Equal(string.Empty, S(b.Get("e")));
        Assert.True(Assert.IsType<YamlScalar>(b.Get("e")).Quoted);
        Assert.Equal("a\"b\\c\n", S(b.Get("q")));
    }

    [Fact]
    public void Parses_literal_blocks()
    {
        YamlMapping root = Map("""
            description: |
              line one
                indented
              line three
            next: 1
            """);
        Assert.Equal("line one\n  indented\nline three\n", S(root.Get("description")));
        Assert.Equal("1", S(root.Get("next")));
    }

    [Theory]
    [InlineData("a: 1\na: 2", "duplicate key")]
    [InlineData("a: 1\n   b: 2", "indentation")]
    [InlineData("a: [1, 2", "unterminated")]
    [InlineData("a: \"open", "unterminated")]
    [InlineData("a: &anchor 1", "unsupported")]
    [InlineData("just text", "expected 'key: value'")]
    [InlineData("a:\n\t- b", "tabs")]
    public void Rejects_malformed_input_with_a_line_number(string text, string message)
    {
        var ex = Assert.Throws<YamlException>(() => MiniYaml.Parse(text));
        Assert.Contains(message, ex.Message, System.StringComparison.Ordinal);
        Assert.True(ex.SourceLine >= 1);
    }

    [Fact]
    public void Writer_output_parses_back_to_the_same_values()
    {
        var w = new YamlWriter();
        w.Add("schema", "x/1");
        w.Add("number", 1.5);
        w.Add("int", 42);
        w.Add("bool", true);
        w.Add("none", null);
        w.Add("tricky", "a: b # c", "a comment");
        w.Add("numeric_string", "007");
        w.Add("hex", new YamlWriter.Hex(0x03E0));
        w.Add("flow", new YamlWriter.Flow([1, "two, three", new YamlWriter.Hex(0x7C00), null]));
        w.Map("nested", n => n.Add("k", "v").Map("deeper", d => d.Add("x", -3)));
        w.Add("items", new List<YamlWriter> { new YamlWriter().Add("a", 1).Add("b", 2), new YamlWriter().Add("a", 3) });
        w.Add("rows", new List<YamlWriter.Flow> { new([1, 2]), new([3, 4]) });
        w.Add("empty", new YamlWriter());
        string text = w.ToYaml("header line\nsecond");
        Assert.StartsWith("# header line\n# second\n", text, System.StringComparison.Ordinal);

        YamlMapping root = Map(text);
        Assert.Equal("1.5", S(root.Get("number")));
        Assert.Equal("42", S(root.Get("int")));
        Assert.Equal("true", S(root.Get("bool")));
        Assert.Equal("a: b # c", S(root.Get("tricky")));
        Assert.Equal("007", S(root.Get("numeric_string")));
        Assert.True(Assert.IsType<YamlScalar>(root.Get("numeric_string")).Quoted);
        Assert.Equal("0x03E0", S(root.Get("hex")));
        Assert.Equal(["1", "two, three", "0x7C00", "null"], Assert.IsType<YamlSequence>(root.Get("flow")).Items.Select(S));
        Assert.Equal("-3", S(Assert.IsType<YamlMapping>(Assert.IsType<YamlMapping>(root.Get("nested")).Get("deeper")).Get("x")));
        var items = Assert.IsType<YamlSequence>(root.Get("items"));
        Assert.Equal("2", S(Assert.IsType<YamlMapping>(items.Items[0]).Get("b")));
        Assert.Equal("3", S(Assert.IsType<YamlMapping>(items.Items[1]).Get("a")));
        var rows = Assert.IsType<YamlSequence>(root.Get("rows"));
        Assert.Equal(["3", "4"], Assert.IsType<YamlSequence>(rows.Items[1]).Items.Select(S));
        Assert.Empty(Assert.IsType<YamlMapping>(root.Get("empty")).Entries);
    }

    [Fact]
    public void Typed_accessors_convert_and_report_errors()
    {
        YamlMapping root = Map("i: 0x1F\nd: -2.5\nb: yes\ns: text");
        Assert.Equal(31, MiniYaml.AsInt(root.Get("i")!, "i"));
        Assert.Equal(-2.5, MiniYaml.AsDouble(root.Get("d")!, "d"));
        Assert.True(MiniYaml.AsBool(root.Get("b")!, "b"));
        Assert.Throws<YamlException>(() => MiniYaml.AsInt(root.Get("s")!, "s"));
        Assert.Throws<YamlException>(() => MiniYaml.AsSequence(root.Get("s")!, "s"));
    }
}
