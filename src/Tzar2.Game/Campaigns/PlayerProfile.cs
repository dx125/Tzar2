using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using Tzar2.Sim.Persistence;

namespace Tzar2.Game.Campaigns;

/// <summary>How a campaign mission ended.</summary>
public enum MissionResult : byte
{
    /// <summary>Won.</summary>
    Won = 0,

    /// <summary>Lost.</summary>
    Lost = 1,

    /// <summary>Left by Load Map without an outcome.</summary>
    Moved = 2,
}

/// <summary>One finished mission in a campaign's history.</summary>
/// <param name="MapId">The campaign map id.</param>
/// <param name="Result">How it ended.</param>
/// <param name="Ticks">Game time it took, in ticks.</param>
public sealed record MissionRecord(string MapId, MissionResult Result, int Ticks);

/// <summary>
/// A player's progress through one campaign (ADR 0133; TDD Appendix G-03): the mission to play next, the missions
/// unlocked and finished, the persistent units and the campaign-scope variables carried between maps.
/// </summary>
/// <param name="CampaignId">The campaign's id.</param>
/// <param name="CampaignHash">The campaign's content hash when progress was saved (a changed campaign is reported, not refused).</param>
/// <param name="Current">The map to play next, or null when the campaign is finished.</param>
/// <param name="Unlocked">Maps the player may start (entry maps and every map reached), in the order they were unlocked.</param>
/// <param name="History">Finished missions in order.</param>
/// <param name="Heroes">Persistent units by name, ascending by name (Appendix C.9).</param>
/// <param name="Variables">Carried variables (<c>#Name</c>, UNVERIFIED campaign scope), ascending by name.</param>
/// <param name="Completed">Whether the campaign was finished.</param>
public sealed record CampaignProgress(
    string CampaignId,
    ulong CampaignHash,
    string? Current,
    ImmutableArray<string> Unlocked,
    ImmutableArray<MissionRecord> History,
    ImmutableArray<PersistentHero> Heroes,
    ImmutableArray<KeyValuePair<string, int>> Variables,
    bool Completed);

/// <summary>
/// The player profile (ADR 0133): name and campaign progress, stored as human-readable, versioned JSON that survives game
/// updates (TDD Appendix G-03, accepted by the Q-08 default). Reading migrates older versions; unknown fields are
/// ignored; every value is bounded (profiles are user files).
/// </summary>
/// <param name="Name">The player's name.</param>
/// <param name="Campaigns">Progress per campaign, ascending by campaign id.</param>
public sealed record PlayerProfile(string Name, ImmutableArray<CampaignProgress> Campaigns)
{
    /// <summary>The current format version.</summary>
    public const int Version = 1;

    /// <summary>Largest profile file accepted.</summary>
    public const int MaxBytes = 16 << 20;

    /// <summary>A new profile.</summary>
    public static PlayerProfile New(string name) => new(name, ImmutableArray<CampaignProgress>.Empty);

    /// <summary>The progress of a campaign, or null.</summary>
    public CampaignProgress? Progress(string campaignId)
    {
        foreach (CampaignProgress p in Campaigns)
        {
            if (string.Equals(p.CampaignId, campaignId, StringComparison.Ordinal))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>This profile with <paramref name="progress"/> replacing the campaign's entry.</summary>
    public PlayerProfile With(CampaignProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var list = new List<CampaignProgress>();
        foreach (CampaignProgress p in Campaigns)
        {
            if (!string.Equals(p.CampaignId, progress.CampaignId, StringComparison.Ordinal))
            {
                list.Add(p);
            }
        }

        list.Add(progress);
        list.Sort(static (a, b) => string.CompareOrdinal(a.CampaignId, b.CampaignId));
        return this with { Campaigns = [.. list] };
    }

    /// <summary>The profile as indented UTF-8 JSON (canonical: equal profiles give equal bytes).</summary>
    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("name", Name);
            w.WriteStartArray("campaigns");
            foreach (CampaignProgress c in Campaigns)
            {
                w.WriteStartObject();
                w.WriteString("id", c.CampaignId);
                w.WriteString("hash", c.CampaignHash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
                if (c.Current is null)
                {
                    w.WriteNull("current");
                }
                else
                {
                    w.WriteString("current", c.Current);
                }

                w.WriteBoolean("completed", c.Completed);
                w.WriteStartArray("unlocked");
                foreach (string u in c.Unlocked)
                {
                    w.WriteStringValue(u);
                }

                w.WriteEndArray();
                w.WriteStartArray("history");
                foreach (MissionRecord m in c.History)
                {
                    w.WriteStartObject();
                    w.WriteString("map", m.MapId);
                    w.WriteString("result", m.Result.ToString().ToLowerInvariant());
                    w.WriteNumber("ticks", m.Ticks);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("heroes");
                foreach (PersistentHero h in c.Heroes)
                {
                    w.WriteStartObject();
                    w.WriteString("name", h.Name);
                    w.WriteString("type", h.TypeKey);
                    w.WriteNumber("level", h.Level);
                    w.WriteNumber("experience", h.Experience);
                    w.WriteBoolean("hero", h.Hero);
                    w.WriteNumber("bonusAttack", h.BonusAttack);
                    w.WriteNumber("bonusDefence", h.BonusDefence);
                    w.WriteStartArray("items");
                    foreach (PersistentItem i in h.Items)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", i.TypeKey);
                        w.WriteNumber("power", i.Power);
                        w.WriteNumber("charges", i.Charges);
                        if (i.SpellKey is not null)
                        {
                            w.WriteString("spell", i.SpellKey);
                        }

                        if (i.Effect != Content.Compiled.ItemEffect.Default)
                        {
                            w.WriteNumber("effect", (int)i.Effect);
                        }

                        if (i.Look != 0)
                        {
                            w.WriteNumber("look", i.Look);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartObject("variables");
                foreach (KeyValuePair<string, int> v in c.Variables)
                {
                    w.WriteNumber(v.Key, v.Value);
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>Reads a profile written by <see cref="ToJson"/> (or an older version, migrated).</summary>
    /// <exception cref="InvalidDataException">The file is not a valid profile.</exception>
    public static PlayerProfile FromJson(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw new InvalidDataException("The profile is too large.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            int version = root.GetProperty("version").GetInt32();
            if (version < 1 || version > Version)
            {
                throw new InvalidDataException($"Profile version {version} is not supported (this game reads 1-{Version}).");
            }

            string name = Text(root, "name", 256);
            var campaigns = new List<CampaignProgress>();
            foreach (JsonElement c in Elements(root, "campaigns", 4096))
            {
                var unlocked = ImmutableArray.CreateBuilder<string>();
                foreach (JsonElement u in Elements(c, "unlocked", 4096))
                {
                    unlocked.Add(Bounded(u.GetString(), 256));
                }

                var history = ImmutableArray.CreateBuilder<MissionRecord>();
                foreach (JsonElement m in Elements(c, "history", 65536))
                {
                    MissionResult result = Text(m, "result", 16) switch
                    {
                        "won" => MissionResult.Won,
                        "lost" => MissionResult.Lost,
                        "moved" => MissionResult.Moved,
                        var other => throw new InvalidDataException($"Unknown mission result '{other}'."),
                    };
                    history.Add(new MissionRecord(Text(m, "map", 256), result, Number(m, "ticks", 0, int.MaxValue)));
                }

                var heroes = new List<PersistentHero>();
                foreach (JsonElement h in Elements(c, "heroes", PersistentHeroes.MaxRecords))
                {
                    var items = ImmutableArray.CreateBuilder<PersistentItem>();
                    foreach (JsonElement i in Elements(h, "items", Content.Compiled.Ruleset.InventoryLimit))
                    {
                        items.Add(new PersistentItem(
                            Text(i, "type", 256),
                            Number(i, "power", 0, 1_000_000),
                            Number(i, "charges", 0, 1_000_000),
                            i.TryGetProperty("spell", out _) ? Text(i, "spell", 256) : null,
                            i.TryGetProperty("effect", out _) ? (Content.Compiled.ItemEffect)Number(i, "effect", 0, (int)Content.Compiled.ItemEffect.Mana) : Content.Compiled.ItemEffect.Default,
                            i.TryGetProperty("look", out _) ? Number(i, "look", 0, 255) : 0));
                    }

                    heroes.Add(new PersistentHero(
                        Text(h, "name", 256),
                        Text(h, "type", 256),
                        Number(h, "level", 0, 10_000),
                        Number(h, "experience", 0, int.MaxValue),
                        h.GetProperty("hero").GetBoolean(),
                        Number(h, "bonusAttack", 0, 100_000),
                        Number(h, "bonusDefence", 0, 100_000),
                        items.ToImmutable()));
                }

                heroes.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                var variables = new List<KeyValuePair<string, int>>();
                if (c.TryGetProperty("variables", out JsonElement vars))
                {
                    foreach (JsonProperty v in vars.EnumerateObject())
                    {
                        if (variables.Count >= 4096)
                        {
                            throw new InvalidDataException("Too many carried variables.");
                        }

                        variables.Add(new(Bounded(v.Name, 256), v.Value.GetInt32()));
                    }
                }

                variables.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                string hash = Text(c, "hash", 16);
                campaigns.Add(new CampaignProgress(
                    Text(c, "id", 256),
                    ulong.Parse(hash, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                    c.GetProperty("current").ValueKind == JsonValueKind.Null ? null : Text(c, "current", 256),
                    unlocked.ToImmutable(),
                    history.ToImmutable(),
                    [.. heroes],
                    [.. variables],
                    c.GetProperty("completed").GetBoolean()));
            }

            campaigns.Sort(static (a, b) => string.CompareOrdinal(a.CampaignId, b.CampaignId));
            for (int i = 1; i < campaigns.Count; i++)
            {
                if (campaigns[i].CampaignId == campaigns[i - 1].CampaignId)
                {
                    throw new InvalidDataException($"Campaign '{campaigns[i].CampaignId}' appears twice in the profile.");
                }
            }

            return new PlayerProfile(name, [.. campaigns]);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The profile is not valid JSON.", e);
        }
        catch (KeyNotFoundException e)
        {
            throw new InvalidDataException("The profile misses a field.", e);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("A profile field has the wrong type.", e);
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("A profile field has the wrong format.", e);
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement parent, string name, int max)
    {
        JsonElement array = parent.GetProperty(name);
        if (array.GetArrayLength() > max)
        {
            throw new InvalidDataException($"Too many entries in '{name}'.");
        }

        return array.EnumerateArray();
    }

    private static string Text(JsonElement parent, string name, int max) => Bounded(parent.GetProperty(name).GetString(), max);

    private static string Bounded(string? value, int max)
        => value is not null && value.Length <= max ? value : throw new InvalidDataException("A profile text is missing or too long.");

    private static int Number(JsonElement parent, string name, int min, int max)
    {
        int value = parent.GetProperty(name).GetInt32();
        return value >= min && value <= max ? value : throw new InvalidDataException($"Profile value {name} = {value} is out of range.");
    }
}

/// <summary>
/// Reads and writes the profile file (<c>profile.json</c> in the user folder). Writes go to a temporary file first and
/// replace the old one only when complete, so a crash never leaves a half-written profile.
/// </summary>
public sealed class ProfileStore
{
    /// <summary>Creates a store in <paramref name="directory"/>.</summary>
    public ProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    /// <summary>The folder.</summary>
    public string Directory { get; }

    /// <summary>The profile file.</summary>
    public string Path => System.IO.Path.Combine(Directory, "profile.json");

    /// <summary>The stored profile, or a new one named <paramref name="defaultName"/> when there is none.</summary>
    /// <exception cref="InvalidDataException">The stored profile is corrupt (it is left in place).</exception>
    public PlayerProfile Load(string defaultName = "Player")
        => File.Exists(Path) ? PlayerProfile.FromJson(File.ReadAllBytes(Path)) : PlayerProfile.New(defaultName);

    /// <summary>Stores <paramref name="profile"/>.</summary>
    public void Save(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        System.IO.Directory.CreateDirectory(Directory);
        string temp = Path + ".tmp";
        File.WriteAllBytes(temp, profile.ToJson());
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>A save-game path for a slot name (letters, digits, '-' and '_' only).</summary>
    public string SavePath(string slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        var name = new StringBuilder();
        foreach (char ch in slot)
        {
            name.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return System.IO.Path.Combine(Directory, "saves", name + ".t2sav");
    }
}
