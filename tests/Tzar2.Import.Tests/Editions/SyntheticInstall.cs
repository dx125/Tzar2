using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tzar2.Import.Tests.Editions;

/// <summary>A temporary directory with generated files and a matching manifest (REQ-LEGAL-03: synthetic fixtures only).</summary>
internal sealed class SyntheticInstall : IDisposable
{
    public SyntheticInstall()
    {
        Root = Path.Combine(Path.GetTempPath(), "tzar2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public List<(string Path, byte[] Content)> Files { get; } = [];

    public SyntheticInstall Add(string relativePath, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Files.Add((relativePath, bytes));
        Write(relativePath, bytes);
        return this;
    }

    public void Write(string relativePath, byte[] bytes)
    {
        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Delete(string relativePath) => File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The manifest of <see cref="Files"/> as written by <see cref="Add"/>.</summary>
    public string ManifestText()
    {
        var sb = new StringBuilder("path\tsize\tmd5\n");
        foreach ((string path, byte[] content) in Files)
        {
            sb.Append(path).Append('\t').Append(content.Length).Append('\t')
              .Append(Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant()).Append('\n');
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }
}
