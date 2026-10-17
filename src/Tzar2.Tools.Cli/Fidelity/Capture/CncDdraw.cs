using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>
/// cnc-ddraw (MIT, github.com/FunkyFr3sh/cnc-ddraw): a DirectDraw re-implementation that runs the 2000-era game in a
/// normal window on modern Windows, so its client area can be captured and receives input like any window. It is
/// downloaded at capture time from the pinned release and verified by SHA-256, never committed, and only placed in
/// the session's work copy (ADR 0030).
/// </summary>
internal static class CncDdraw
{
    public const string Version = "7.1.0.0";
    public const string Url = "https://github.com/FunkyFr3sh/cnc-ddraw/releases/download/v7.1.0.0/cnc-ddraw.zip";
    public const string ZipSha256 = "0b13ab89a64c9918189b1dadd449ef6ed3cb3b7b19cabd96d8adbd95505bb908";
    public const string DllSha256 = "85e0f7d530dfda134793a57cb3e76b0287dcc96892ee57162dd68f47283b03a9";

    /// <summary>
    /// The <c>[ddraw]</c> settings the harness writes (scenario <c>wrapper:</c> keys override them): a borderless-free
    /// window at the game's own size, the GDI renderer (no shader, no scaling, so the client area holds the game's
    /// pixels 1:1), no saved window state, no compatibility pop-ups.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windowed"] = "true",
        ["fullscreen"] = "false",
        ["width"] = "0",
        ["height"] = "0",
        ["maintas"] = "false",
        ["boxing"] = "false",
        ["border"] = "true",
        ["resizable"] = "false",
        ["renderer"] = "gdi",
        ["adjmouse"] = "false",
        ["devmode"] = "false",
        ["savesettings"] = "0",
        ["posX"] = "-32000",
        ["posY"] = "-32000",
        ["center_window"] = "2",
        ["vsync"] = "false",
        ["maxgameticks"] = "0",
        ["nonexclusive"] = "false",
        ["singlecpu"] = "true",
        ["rgb555"] = "false",
        ["no_compat_warning"] = "true",
    };

    /// <summary>The cached, verified release zip; downloads it when missing.</summary>
    /// <exception cref="InvalidDataException">The download does not match the pinned hash.</exception>
    public static string EnsureDownloaded(string cacheDir, HttpClient http, CaptureLog log)
    {
        Directory.CreateDirectory(cacheDir);
        string zip = Path.Combine(cacheDir, $"cnc-ddraw-{Version}.zip");
        if (File.Exists(zip) && Sha256(File.ReadAllBytes(zip)) == ZipSha256)
        {
            return zip;
        }

        log.Info($"Downloading cnc-ddraw {Version} (MIT) from GitHub ...");
        byte[] data = http.GetByteArrayAsync(new Uri(Url)).GetAwaiter().GetResult();
        string actual = Sha256(data);
        if (actual != ZipSha256)
        {
            throw new InvalidDataException($"cnc-ddraw download has SHA-256 {actual}, expected {ZipSha256}; not using it");
        }

        File.WriteAllBytes(zip, data);
        return zip;
    }

    /// <summary>Puts <c>ddraw.dll</c> from the verified zip into the work copy.</summary>
    public static void Install(string zipPath, string workDir)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry dll = archive.GetEntry("ddraw.dll") ?? throw new InvalidDataException("cnc-ddraw zip has no ddraw.dll");
        using var memory = new MemoryStream();
        using (Stream s = dll.Open())
        {
            s.CopyTo(memory);
        }

        byte[] bytes = memory.ToArray();
        if (Sha256(bytes) != DllSha256)
        {
            throw new InvalidDataException("ddraw.dll inside the cnc-ddraw zip does not match the pinned hash");
        }

        File.WriteAllBytes(Path.Combine(workDir, "ddraw.dll"), bytes);
    }

    /// <summary>The <c>ddraw.ini</c> text for the given overrides.</summary>
    public static string IniText(IReadOnlyDictionary<string, string> overrides)
    {
        var sb = new StringBuilder();
        sb.Append("; Written by t2 fidelity capture (Tzar2 capture harness, ADR 0030). Recreated before every launch.\r\n");
        sb.Append("[ddraw]\r\n");
        var merged = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        foreach ((string k, string v) in overrides)
        {
            merged[k] = v;
        }

        foreach ((string k, string v) in merged)
        {
            sb.Append(k).Append('=').Append(v).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Whether a <c>ddraw.dll</c> looks like cnc-ddraw (its name appears in the binary).</summary>
    public static bool LooksLikeCncDdraw(string dllPath)
    {
        byte[] data = File.ReadAllBytes(dllPath);
        return data.AsSpan().IndexOf("cnc-ddraw"u8) >= 0 || data.AsSpan().IndexOf("cnc_ddraw"u8) >= 0;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
