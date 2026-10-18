using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Kingdoms;

/// <summary>A kingdom's stance towards another (GAME-13; manual ch. 5 §8). Values are wire and snapshot format.</summary>
public enum Stance : byte
{
    /// <summary>Military units attack all buildings and units in their sight.</summary>
    Enemy = 0,

    /// <summary>Military units attack only military units in their sight.</summary>
    Neutral = 1,

    /// <summary>Military units do not attack the other kingdom's buildings and units.</summary>
    Ally = 2,
}

/// <summary>What a kingdom offers an ally (GAME-13; each takes effect only when both offer it and both are allies).</summary>
[Flags]
public enum Sharing : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Shared vision.</summary>
    Vision = 1,

    /// <summary>Shared resources: either may spend the other's stockpile when its own does not cover a cost.</summary>
    Resources = 2,

    /// <summary>Shared unit control: either may command the other's units.</summary>
    UnitControl = 4,

    /// <summary>Allied (shared) victory: the team wins and loses together (set per kingdom towards everyone).</summary>
    Victory = 8,
}

/// <summary>
/// Asymmetric stances and consented sharing between the kingdoms of a match (GAME-13, ADR 0102). Every change is a
/// command (rate-limited per pair: no flip-flop spam); this class only holds and answers. Nature is everyone's enemy.
/// </summary>
public sealed class Diplomacy
{
    private const Sharing KnownSharing = Sharing.Vision | Sharing.Resources | Sharing.UnitControl | Sharing.Victory;

    /// <summary>The change tick of a self-entry or a pair that was never changed (before any valid tick).</summary>
    internal const int NeverChangedTick = int.MinValue / 2;

    private readonly int _players;
    private readonly Stance[] _stances;
    private readonly Sharing[] _offers;
    private readonly int[] _changedTick;
    private readonly uint[] _visionSources;

    internal Diplomacy(int players)
    {
        _players = players;
        _stances = new Stance[players * players];
        _offers = new Sharing[players * players];
        _changedTick = new int[players * players];
        _visionSources = new uint[players];
        _changedTick.AsSpan().Fill(NeverChangedTick);
        Refresh();
    }

    /// <summary>Kingdoms in the match.</summary>
    public int PlayerCount => _players;

    /// <summary>The stance of <paramref name="from"/> towards <paramref name="to"/> (own kingdom: Ally; Nature, or towards Nature: Enemy).</summary>
    public Stance StanceOf(int from, int to)
    {
        if (from == to)
        {
            return Stance.Ally;
        }

        return (uint)from < (uint)_players && (uint)to < (uint)_players ? _stances[(from * _players) + to] : Stance.Enemy;
    }

    /// <summary>What <paramref name="from"/> offers <paramref name="to"/>.</summary>
    public Sharing OffersOf(int from, int to)
        => (uint)from < (uint)_players && (uint)to < (uint)_players ? _offers[(from * _players) + to] : Sharing.None;

    /// <summary>Whether both kingdoms treat each other as allies (a kingdom is its own ally).</summary>
    public bool AreMutualAllies(int a, int b) => StanceOf(a, b) == Stance.Ally && StanceOf(b, a) == Stance.Ally;

    /// <summary>Whether <paramref name="feature"/> is shared between two kingdoms: mutual allies who both offer it (always for oneself).</summary>
    public bool Shares(int a, int b, Sharing feature)
        => a == b || (AreMutualAllies(a, b) && (OffersOf(a, b) & feature) == feature && (OffersOf(b, a) & feature) == feature);

    /// <summary>The kingdoms whose vision <paramref name="player"/> sees (bit per slot; includes itself).</summary>
    public uint VisionSources(int player) => (uint)player < (uint)_players ? _visionSources[player] : 0;

    /// <summary>
    /// Whether units of <paramref name="attackerOwner"/> may attack an entity of <paramref name="targetOwner"/> (manual ch. 5
    /// §8): enemies anything, neutrals only military units, allies nothing; Nature fights and is fought by everyone.
    /// </summary>
    public bool MayAttack(int attackerOwner, int targetOwner, bool targetIsMilitary)
    {
        if (attackerOwner == targetOwner)
        {
            return false;
        }

        if ((uint)attackerOwner >= (uint)_players || (uint)targetOwner >= (uint)_players)
        {
            return true;
        }

        return StanceOf(attackerOwner, targetOwner) switch
        {
            Stance.Enemy => true,
            Stance.Neutral => targetIsMilitary,
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="player"/> may command units of <paramref name="owner"/> (own, or shared unit control).</summary>
    public bool MayCommand(int player, int owner) => player == owner || ((uint)owner < (uint)_players && Shares(player, owner, Sharing.UnitControl));

    /// <summary>Whether <paramref name="owner"/>'s gates let units of <paramref name="other"/> through (owner and the kingdoms it treats as allies).</summary>
    public bool OpensGateFor(int owner, int other) => owner == other || ((uint)owner < (uint)_players && StanceOf(owner, other) == Stance.Ally);

    /// <summary>The tick of the last stance or sharing change of <paramref name="from"/> towards <paramref name="to"/>.</summary>
    public int LastChangeTick(int from, int to) => _changedTick[(from * _players) + to];

    internal void Set(int from, int to, Stance stance, Sharing offers, int tick)
    {
        int i = (from * _players) + to;
        _stances[i] = stance;
        _offers[i] = offers;
        _changedTick[i] = tick;
        Refresh();
    }

    // Match start: teams are mutual allies with shared vision and allied victory (lobby teams, SimulationConfig.Teams).
    internal void ApplyTeams(SimulationConfig config)
    {
        for (int a = 0; a < _players; a++)
        {
            for (int b = 0; b < _players; b++)
            {
                if (a != b && config.TeamOf(a) != 0 && config.TeamOf(a) == config.TeamOf(b))
                {
                    _stances[(a * _players) + b] = Stance.Ally;
                    _offers[(a * _players) + b] = Sharing.Vision | Sharing.Victory;
                }
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        for (int p = 0; p < _players; p++)
        {
            uint mask = 1u << p;
            for (int q = 0; q < _players; q++)
            {
                if (q != p && Shares(p, q, Sharing.Vision))
                {
                    mask |= 1u << q;
                }
            }

            _visionSources[p] = mask;
        }
    }

    internal void AppendTo(StateHasher hasher)
    {
        hasher.AddSpan<byte>(MemoryMarshal.AsBytes(_stances.AsSpan())).AddSpan<byte>(MemoryMarshal.AsBytes(_offers.AsSpan())).AddSpan<int>(_changedTick);
    }

    internal void WriteTo(BinaryBufferWriter writer)
    {
        writer.WriteBytes(MemoryMarshal.AsBytes(_stances.AsSpan()));
        writer.WriteBytes(MemoryMarshal.AsBytes(_offers.AsSpan()));
        writer.WriteBytes(MemoryMarshal.AsBytes(_changedTick.AsSpan()));
    }

    internal void ReadFrom(ref BinaryBufferReader reader, int tick)
    {
        reader.ReadBytes(_stances.Length).CopyTo(MemoryMarshal.AsBytes(_stances.AsSpan()));
        reader.ReadBytes(_offers.Length).CopyTo(MemoryMarshal.AsBytes(_offers.AsSpan()));
        reader.ReadBytes(_changedTick.Length * sizeof(int)).CopyTo(MemoryMarshal.AsBytes(_changedTick.AsSpan()));
        for (int i = 0; i < _stances.Length; i++)
        {
            bool self = i / _players == i % _players;
            if (_stances[i] > Stance.Ally || (_offers[i] & ~KnownSharing) != 0 || _changedTick[i] > tick
                || (self && (_stances[i] != Stance.Enemy || _offers[i] != Sharing.None || _changedTick[i] != NeverChangedTick)))
            {
                throw new InvalidDataException("Snapshot violates a world invariant: invalid diplomacy.");
            }
        }

        Refresh();
    }
}
