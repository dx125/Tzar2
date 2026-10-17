using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tzar2.Import.Editions;
using Tzar2.Tools.Cli.Commands;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public sealed class VerifyOriginalCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tzar2-cli-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _manifest;

    public VerifyOriginalCommandTests()
    {
        // Synthetic install and manifest only (REQ-LEGAL-03); never real game files.
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(_install, "DATA"));
        var manifest = new StringBuilder("path\tsize\tmd5\n");
        foreach ((string path, string content) in new[] { ("Tzar.exe", "synthetic exe"), ("DATA/RES.INI", "[resources]") })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(Path.Combine(_install, path), bytes);
            manifest.Append(path).Append('\t').Append(bytes.Length).Append('\t')
                .Append(Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant()).Append('\n');
        }

        _manifest = Path.Combine(_root, "synthetic.tsv");
        File.WriteAllText(_manifest, manifest.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Matching_install_exits_zero()
    {
        (int code, string output, _) = Run("verify-original", _install, "--manifest", _manifest);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("Identical: 2, modified: 0, missing: 0, extra: 0", output, StringComparison.Ordinal);
        Assert.Contains("matches the reference build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Modified_and_missing_files_exit_one_and_are_listed()
    {
        File.WriteAllText(Path.Combine(_install, "Tzar.exe"), "patched exe!!");
        File.Delete(Path.Combine(_install, "DATA", "RES.INI"));
        (int code, string output, _) = Run("verify-original", _install, "--manifest", _manifest);
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("MODIFIED  Tzar.exe", output, StringComparison.Ordinal);
        Assert.Contains("MISSING   DATA/RES.INI", output, StringComparison.Ordinal);
        Assert.Contains("Result: MISMATCH.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_files_fail_only_in_strict_mode()
    {
        File.WriteAllText(Path.Combine(_install, "readme.txt"), "extra");
        (int code, string output, _) = Run("verify-original", _install, "--manifest", _manifest);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("EXTRA     readme.txt", output, StringComparison.Ordinal);

        (int strictCode, _, _) = Run("verify-original", "--strict", _install, "--manifest", _manifest);
        Assert.Equal(ExitCodes.Mismatch, strictCode);
    }

    [Fact]
    public void Quiet_prints_only_the_summary()
    {
        File.Delete(Path.Combine(_install, "Tzar.exe"));
        (int code, string output, _) = Run("verify-original", _install, "--manifest", _manifest, "--quiet");
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.DoesNotContain("MISSING", output, StringComparison.Ordinal);
        Assert.Contains("missing: 1", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("verify-original")]
    [InlineData("verify-original", "--manifest")]
    [InlineData("verify-original", "a", "b")]
    [InlineData("verify-original", "--bogus", "a")]
    public void Bad_arguments_exit_two(params string[] args) => Assert.Equal(ExitCodes.UsageError, Run(args).Code);

    [Fact]
    public void Manifest_option_does_not_consume_the_next_option()
    {
        (int code, _, string error) = Run("verify-original", _install, "--manifest", "--strict");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("'--manifest' requires a file", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("manifest")]
    public void Nul_in_a_path_exits_two(string which)
    {
        string[] args = which == "install"
            ? ["verify-original", _install + "\0x", "--manifest", _manifest]
            : ["verify-original", _install, "--manifest", _manifest + "\0x"];
        (int code, _, string error) = Run(args);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.StartsWith("t2 verify-original: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonexistent_install_or_manifest_exits_two()
    {
        (int code, _, string error) = Run("verify-original", Path.Combine(_root, "missing"), "--manifest", _manifest);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run("verify-original", _install, "--manifest", Path.Combine(_root, "none.tsv")).Code);
    }

    [Fact]
    public void Embedded_manifest_is_the_gog_reference_build()
    {
        // Metadata only: paths, sizes and hashes of the reference build (docs/original-build/README.md §2).
        ReferenceManifest manifest = VerifyOriginalCommand.LoadEmbeddedManifest();
        Assert.Equal("gog-1.10-en.reference.tsv", manifest.Name);
        Assert.Equal(2916, manifest.Entries.Count);
        Assert.Contains(manifest.Entries, e => e.Path == "DATA/AI/AI.INI");
    }

    [Fact]
    public void Default_manifest_reports_everything_missing_for_an_unrelated_directory()
    {
        (int code, string output, _) = Run("verify-original", _install, "--quiet");
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("Reference: gog-1.10-en.reference.tsv (2916 files", output, StringComparison.Ordinal);
    }
}
