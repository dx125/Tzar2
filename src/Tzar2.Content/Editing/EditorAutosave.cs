using System;

namespace Tzar2.Content.Editing;

/// <summary>
/// When the editor writes its autosave (ADR 0286): every <see cref="IntervalSeconds"/> of editing, if the work has unsaved
/// changes made since the last autosave. The autosave never replaces the player's own file: it goes to
/// <see cref="Folder"/>, one file per map or campaign name, and the editor opens it from there on request.
/// </summary>
public sealed class EditorAutosave
{
    /// <summary>Seconds between two autosaves.</summary>
    public const int IntervalSeconds = 180;

    /// <summary>The folder of the user data the autosaves go to.</summary>
    public const string Folder = "autosave";

    private long _dueMs = IntervalSeconds * 1000L;
    private long? _written;

    /// <summary>
    /// Whether to write the autosave now: at most once per interval, when the work is dirty and its version is not the
    /// one written last. A true answer counts as written.
    /// </summary>
    /// <param name="nowMs">Milliseconds of editing so far.</param>
    /// <param name="dirty">Whether the work has unsaved changes.</param>
    /// <param name="version">A value that changes with every edit.</param>
    public bool Due(long nowMs, bool dirty, long version)
    {
        if (nowMs < _dueMs)
        {
            return false;
        }

        _dueMs = nowMs + (IntervalSeconds * 1000L);
        if (!dirty || _written == version)
        {
            return false;
        }

        _written = version;
        return true;
    }

    /// <summary>The autosave file of a map.</summary>
    public static string MapFile(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return MapPackage.Slug(document.State.Name) + ".t2map";
    }

    /// <summary>The autosave file of a campaign.</summary>
    public static string CampaignFile(CampaignDocument campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return MapPackage.Slug(campaign.State.Id) + ".t2camp";
    }
}
