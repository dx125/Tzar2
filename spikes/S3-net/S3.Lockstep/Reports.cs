using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Tzar2.Spikes.S3.Lockstep;

/// <summary>Per-client measurements written at the end of a match.</summary>
public sealed class ClientReport
{
    /// <summary>Slot number.</summary>
    public int Slot { get; set; }

    /// <summary>OS and architecture the client ran on.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Ticks executed.</summary>
    public int TicksExecuted { get; set; }

    /// <summary>Wall time from the first to the last executed tick, in ms.</summary>
    public double DurationMs { get; set; }

    /// <summary>Achieved ticks per second.</summary>
    public double TickRate { get; set; }

    /// <summary>Gaps between consecutive tick executions longer than the stall threshold.</summary>
    public int Stalls { get; set; }

    /// <summary>The first stalls: tick executed after the gap, and the gap.</summary>
    public List<string> StallLog { get; set; } = [];

    /// <summary>Sum over stalls of (gap - tick length), in ms.</summary>
    public double StallMs { get; set; }

    /// <summary>Longest gap between two executed ticks, in ms.</summary>
    public double MaxGapMs { get; set; }

    /// <summary>Commands this client issued.</summary>
    public int CommandsIssued { get; set; }

    /// <summary>Own commands that came back in a closed tick and were executed.</summary>
    public int CommandsExecuted { get; set; }

    /// <summary>Mean issue-to-execution latency of own commands, in ms.</summary>
    public double LatencyMeanMs { get; set; }

    /// <summary>Median latency, in ms.</summary>
    public double LatencyP50Ms { get; set; }

    /// <summary>95th percentile latency, in ms.</summary>
    public double LatencyP95Ms { get; set; }

    /// <summary>Maximum latency, in ms.</summary>
    public double LatencyMaxMs { get; set; }

    /// <summary>Mean input delay D over all batches, in ticks.</summary>
    public double DelayMean { get; set; }

    /// <summary>Lowest D used.</summary>
    public int DelayMin { get; set; }

    /// <summary>Highest D used.</summary>
    public int DelayMax { get; set; }

    /// <summary>Batches the relay reported as late.</summary>
    public int LateBatches { get; set; }

    /// <summary>Batches sent.</summary>
    public int BatchesSent { get; set; }

    /// <summary>Final smoothed RTT, in ms.</summary>
    public double SrttMs { get; set; }

    /// <summary>Final RTT variation, in ms.</summary>
    public double RttVarMs { get; set; }

    /// <summary>LiteNetLib UDP payload bytes sent.</summary>
    public long BytesSent { get; set; }

    /// <summary>LiteNetLib UDP payload bytes received.</summary>
    public long BytesReceived { get; set; }

    /// <summary>LiteNetLib packets sent.</summary>
    public long PacketsSent { get; set; }

    /// <summary>LiteNetLib packets received.</summary>
    public long PacketsReceived { get; set; }

    /// <summary>Desync reports received from the relay.</summary>
    public List<int> DesyncTicks { get; set; } = [];

    /// <summary>Time from starting to reconnect until caught up with the live tick, in ms (-1 = no rejoin).</summary>
    public double RejoinMs { get; set; } = -1;

    /// <summary>Ticks fast-forwarded after a rejoin.</summary>
    public int RejoinBacklogTicks { get; set; }

    /// <summary>The snapshot received on rejoin matched its announced hash.</summary>
    public bool RejoinSnapshotVerified { get; set; }

    /// <summary>Last executed tick.</summary>
    public int FinalTick { get; set; } = -1;

    /// <summary>State hash after <see cref="FinalTick"/>, hex.</summary>
    public string FinalHash { get; set; } = string.Empty;
}

/// <summary>Relay-side summary of one match.</summary>
public sealed class RelayReport
{
    /// <summary>Lobby code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Players.</summary>
    public int Players { get; set; }

    /// <summary>Last closed tick.</summary>
    public int LastClosedTick { get; set; }

    /// <summary>Ticks whose hashes were compared.</summary>
    public int HashTicksCompared { get; set; }

    /// <summary>Detected desyncs.</summary>
    public List<DesyncEntry> Desyncs { get; set; } = [];

    /// <summary>Late batches per slot.</summary>
    public List<int> LateBatches { get; set; } = [];

    /// <summary>Batches received per slot.</summary>
    public List<int> Batches { get; set; } = [];

    /// <summary>Completed rejoins.</summary>
    public int Rejoins { get; set; }

    /// <summary>Relay-side time from a rejoin hello to the resume being sent, ms.</summary>
    public List<double> RejoinServeMs { get; set; } = [];
}

/// <summary>One detected desync.</summary>
public sealed class DesyncEntry
{
    /// <summary>First tick with disagreeing hashes.</summary>
    public int Tick { get; set; }

    /// <summary>Slots outside the majority.</summary>
    public List<int> OddSlots { get; set; } = [];

    /// <summary>Relay time from closing the tick to detecting the mismatch, ms.</summary>
    public double DetectedAfterMs { get; set; }
}

/// <summary>Source-generated JSON (AOT/trimming safe, REQ-ARCH-02).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientReport))]
[JsonSerializable(typeof(RelayReport))]
[JsonSerializable(typeof(List<RelayReport>))]
public sealed partial class ReportJson : JsonSerializerContext;
