using System;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;

namespace Tzar2.Spikes.S3.Sim;

/// <summary>Kinds of toy commands.</summary>
public enum SimCommandKind : byte
{
    /// <summary>Move a range of the player's units to a point.</summary>
    Move = 1,

    /// <summary>Send a range of the player's units to random points near them (uses the sim RNG).</summary>
    Scatter = 2,
}

/// <summary>
/// One toy command. <see cref="Id"/> is the issuing client's command counter; the sim ignores it, the lockstep
/// client uses it to measure input latency.
/// </summary>
public readonly record struct SimCommand(byte Player, SimCommandKind Kind, ushort FirstUnit, ushort UnitCount, int X, int Y, uint Id);

/// <summary>
/// A toy deterministic simulation for the lockstep spike: every player owns <see cref="UnitsPerPlayer"/> units that
/// walk to targets in <see cref="Fixed"/> coordinates, and one random unit per tick takes damage from the combat RNG.
/// It is just complex enough that any missed, reordered or duplicated command changes the state hash.
/// </summary>
public sealed class ToySim
{
    /// <summary>Units per player.</summary>
    public const int UnitsPerPlayer = 64;

    /// <summary>World edge length in world units (64 tiles of 2,048 units).</summary>
    public const int WorldSize = 64 * 2048;

    private const int SnapshotVersion = 1;
    private const int MaxHp = 100;

    private static readonly Fixed Speed = Fixed.FromInt(96);

    private readonly Unit[] _units;
    private readonly StateHasher _hasher = new();
    private readonly RngStreams _rng;

    /// <summary>Creates the initial state of a match.</summary>
    public ToySim(ulong seed, int playerCount)
        : this(new RngStreams(seed, playerCount), new Unit[playerCount * UnitsPerPlayer], 0)
    {
        for (int i = 0; i < _units.Length; i++)
        {
            (Fixed x, Fixed y) = SpawnPoint(i);
            _units[i] = new Unit { X = x, Y = y, TargetX = x, TargetY = y, Hp = MaxHp };
        }
    }

    private ToySim(RngStreams rng, Unit[] units, int nextTick)
    {
        _rng = rng;
        _units = units;
        NextTick = nextTick;
    }

    /// <summary>Number of players.</summary>
    public int PlayerCount => _rng.PlayerCount;

    /// <summary>The tick the next call to <see cref="Tick"/> executes (= number of ticks executed).</summary>
    public int NextTick { get; private set; }

    /// <summary>Executes one tick with the relay-ordered commands of that tick.</summary>
    public void Tick(ReadOnlySpan<SimCommand> commands)
    {
        foreach (SimCommand command in commands)
        {
            Apply(command);
        }

        for (int i = 0; i < _units.Length; i++)
        {
            Move(ref _units[i]);
        }

        int victim = _rng.Combat.NextInt(_units.Length);
        ref Unit unit = ref _units[victim];
        unit.Hp -= _rng.Combat.NextInt(1, 6);
        if (unit.Hp <= 0)
        {
            (Fixed x, Fixed y) = SpawnPoint(victim);
            unit = new Unit { X = x, Y = y, TargetX = x, TargetY = y, Hp = MaxHp };
        }

        NextTick++;
    }

    /// <summary>xxHash64 of the full state after the last executed tick (REQ-SIM-12).</summary>
    public ulong StateHash()
    {
        _hasher.Add(NextTick);
        foreach (Unit unit in _units)
        {
            _hasher.Add(unit.X).Add(unit.Y).Add(unit.TargetX).Add(unit.TargetY).Add(unit.Hp);
        }

        _rng.AppendTo(_hasher);
        return _hasher.GetHashAndReset();
    }

    /// <summary>Deliberately corrupts the state by one raw unit, to prove desync detection.</summary>
    public void ForceDesync() => _units[0].X += Fixed.Epsilon;

    /// <summary>Writes a snapshot of the complete state (REQ-SIM-13).</summary>
    public void WriteSnapshot(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64(SnapshotVersion);
        writer.WriteInt32(NextTick);
        _rng.WriteTo(writer);
        writer.WriteVarUInt64((ulong)_units.Length);
        foreach (Unit unit in _units)
        {
            writer.WriteFixed(unit.X);
            writer.WriteFixed(unit.Y);
            writer.WriteFixed(unit.TargetX);
            writer.WriteFixed(unit.TargetY);
            writer.WriteInt32(unit.Hp);
        }
    }

    /// <summary>Restores a snapshot written by <see cref="WriteSnapshot"/>.</summary>
    public static ToySim ReadSnapshot(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryBufferReader(data);
        if (reader.ReadVarUInt64() != SnapshotVersion)
        {
            throw new InvalidOperationException("Unsupported toy snapshot version.");
        }

        int nextTick = reader.ReadInt32();
        RngStreams rng = RngStreams.ReadFrom(ref reader);
        ulong count = reader.ReadVarUInt64();
        if (count != (ulong)(rng.PlayerCount * UnitsPerPlayer))
        {
            throw new InvalidOperationException("Toy snapshot unit count does not match its player count.");
        }

        var units = new Unit[(int)count];
        for (int i = 0; i < units.Length; i++)
        {
            units[i] = new Unit
            {
                X = reader.ReadFixed(),
                Y = reader.ReadFixed(),
                TargetX = reader.ReadFixed(),
                TargetY = reader.ReadFixed(),
                Hp = reader.ReadInt32(),
            };
        }

        return new ToySim(rng, units, nextTick);
    }

    private void Apply(SimCommand command)
    {
        // Commands are re-validated at execution time (TDD 8.6); invalid ones are ignored deterministically.
        if (command.Player >= PlayerCount || command.FirstUnit >= UnitsPerPlayer)
        {
            return;
        }

        int first = (command.Player * UnitsPerPlayer) + command.FirstUnit;
        int end = first + Math.Min((int)command.UnitCount, UnitsPerPlayer - command.FirstUnit);
        for (int i = first; i < end; i++)
        {
            ref Unit unit = ref _units[i];
            switch (command.Kind)
            {
                case SimCommandKind.Move:
                    unit.TargetX = Fixed.FromInt(Math.Clamp(command.X, 0, WorldSize));
                    unit.TargetY = Fixed.FromInt(Math.Clamp(command.Y, 0, WorldSize));
                    break;
                case SimCommandKind.Scatter:
                    unit.TargetX = Fixed.Clamp(unit.X + Fixed.FromInt(_rng.Misc.NextInt(-4096, 4097)), Fixed.Zero, Fixed.FromInt(WorldSize));
                    unit.TargetY = Fixed.Clamp(unit.Y + Fixed.FromInt(_rng.Misc.NextInt(-4096, 4097)), Fixed.Zero, Fixed.FromInt(WorldSize));
                    break;
                default:
                    return;
            }
        }
    }

    private static void Move(ref Unit unit)
    {
        Fixed dx = unit.TargetX - unit.X;
        Fixed dy = unit.TargetY - unit.Y;
        if (dx == Fixed.Zero && dy == Fixed.Zero)
        {
            return;
        }

        Fixed distance = Fixed.Sqrt((dx * dx) + (dy * dy));
        if (distance <= Speed)
        {
            unit.X = unit.TargetX;
            unit.Y = unit.TargetY;
            return;
        }

        unit.X += dx * Speed / distance;
        unit.Y += dy * Speed / distance;
    }

    private static (Fixed X, Fixed Y) SpawnPoint(int unitIndex)
    {
        int player = unitIndex / UnitsPerPlayer;
        int local = unitIndex % UnitsPerPlayer;
        int baseX = (((player % 4) * 16) + 4) * 2048;
        int baseY = (((player / 4) * 32) + 8) * 2048;
        return (Fixed.FromInt(baseX + ((local % 8) * 256)), Fixed.FromInt(baseY + ((local / 8) * 256)));
    }

    private struct Unit
    {
        public Fixed X;
        public Fixed Y;
        public Fixed TargetX;
        public Fixed TargetY;
        public int Hp;
    }
}
