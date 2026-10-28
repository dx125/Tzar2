namespace Tzar2.Sim.Snapshots;

/// <summary>How strictly a snapshot must match the reading version (ADR 0271).</summary>
public enum SnapshotTolerance
{
    /// <summary>The same format, the same rules and the stored hash (replays, rejoins, desync reports).</summary>
    Exact = 0,

    /// <summary>
    /// A player's save: an older format back to <see cref="WorldSnapshot.OldestSaveVersion"/>, or other rules with the
    /// same tables, continues under the current version; the state is still validated.
    /// </summary>
    SaveGame = 1,
}
