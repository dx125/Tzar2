using System;
using System.Runtime.InteropServices;
using Lua;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Spikes.Lua;

namespace Tzar2.Spikes.S2;

/// <summary>Hot unit data. Only 8-byte-then-4-byte fields, so the struct has no padding and can be hashed raw.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Unit
{
    public long Speed; // Fixed raw, pixels per tick
    public int X;
    public int Y;
    public int TargetX;
    public int TargetY;
    public int Hp;
    public int Owner;
    public int Facing; // Angle raw
    public int Kills;
}

/// <summary>
/// A toy lockstep sim: units steer to targets with Atan2/Sin/Cos, measure distance with ISqrt, fight with basis-point
/// chances, and every tick each player's Lua script (sandboxed, budgeted) sets the rally point and aggression.
/// </summary>
internal sealed class ToySim
{
    public const int Players = 8;
    private const int TileUnits = 2048;
    private const int MapUnits = 128 * TileUnits;
    private const long LuaBudget = 20_000;

    private const string PlayerScript = """
        local names = { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet" }
        local visits = {}
        local history = {}
        local memo = setmetatable({}, { __index = function(t, k) local v = (k * 2654435761) % 4294967296 t[k] = v return v end })
        aborted = 0

        function onTick(tick, p, alive, sx, sy, kills)
          -- integer arithmetic carried in doubles (exact below 2^53); '/' then floor is the 5.2 integer division
          local cx = alive > 0 and math.floor(sx / alive) or 65536
          local cy = alive > 0 and math.floor(sy / alive) or 65536

          -- string-keyed table with inserts and deletes; pairs() order feeds the result
          local key = names[(tick * 7 + p) % #names + 1] .. (tick % 13)
          visits[key] = (visits[key] or 0) + 1
          if tick % 3 == 0 then visits[names[(tick + p * 3) % #names + 1] .. ((tick + 5) % 13)] = nil end
          local h = 0
          for k, v in pairs(visits) do h = (h * 31 + #k * 7 + v + string.byte(k, 1)) % 2147483647 end

          -- sort with a Lua comparator, bit32, string ops, metamethods
          history[(tick % 16) + 1] = (h + kills * 977) % 100003
          local sorted = {}
          for i = 1, #history do sorted[i] = history[i] end
          table.sort(sorted, function(a, b) return a > b end)
          local mix = bit32.bxor(sorted[1], bit32.lshift(sorted[#sorted] % 65536, 7), memo[tick % 50])
          local tag = string.format("%d:%s", mix % 1000, string.sub(key, 2, 4))

          -- fixed-point steering through the engine's fx.* (no host transcendental functions)
          local ang = fx.atan2(32768 * 4 - cy, 32768 * 4 - cx)
          local orbit = (tick * 373 + p * 8192) % 65536
          local r = 6 * 2048 + (h % 4096)
          local rx = cx + fx.mul(fx.cos((ang + orbit) % 65536), r * fx.one) / fx.one
          local ry = cy + fx.mul(fx.sin((ang + orbit) % 65536), r * fx.one) / fx.one
          rx = math.floor(rx) ry = math.floor(ry)

          -- deterministic float arithmetic (+ - * / only): result floored before it crosses back
          local f = (sx * 0.37 + sy / 3.1 - alive * 1.5) / (kills + 1.25)
          local wobble = math.floor(f) % 2048

          -- engine RNG; sometimes a script runs away and must be stopped by the budget at the same point everywhere
          if math.random(1, 97) == 1 then
            aborted = aborted + 1
            local n = 0
            while true do n = n + #tag end
          end

          local aggression = 2000 + (h % 5000) + math.random(0, 1000)
          return rx + wobble, ry - wobble, aggression, #tag
        end
        """;

    private readonly Unit[] _units;
    private readonly RngStreams _rng;
    private readonly LuaSandbox[] _scripts = new LuaSandbox[Players];
    private readonly int[] _rallyX = new int[Players];
    private readonly int[] _rallyY = new int[Players];
    private readonly int[] _aggression = new int[Players];
    private readonly int[] _scriptStatus = new int[Players];
    private readonly long[] _scriptCharged = new long[Players];
    private readonly int[] _alive = new int[Players];
    private readonly long[] _sumX = new long[Players];
    private readonly long[] _sumY = new long[Players];
    private readonly int[] _kills = new int[Players];
    private readonly StateHasher _hasher = new();

    public ToySim(ulong seed, int unitCount)
    {
        _rng = new RngStreams(seed, Players);
        _units = new Unit[unitCount];
        for (int i = 0; i < unitCount; i++)
        {
            ref Unit u = ref _units[i];
            u.Owner = i % Players;
            u.X = _rng.MapGen.NextInt(MapUnits);
            u.Y = _rng.MapGen.NextInt(MapUnits);
            u.TargetX = _rng.MapGen.NextInt(MapUnits);
            u.TargetY = _rng.MapGen.NextInt(MapUnits);
            u.Speed = Fixed.FromRatio(_rng.MapGen.NextInt(8, 48), 8).Raw;
            u.Hp = 1000;
        }

        for (int p = 0; p < Players; p++)
        {
            _scripts[p] = new LuaSandbox(_rng.Scripts.NextUInt64());
            ScriptResult load = _scripts[p].DoString(PlayerScript, "=player" + p, 1_000_000);
            if (load.Status != ScriptStatus.Ok)
            {
                throw new InvalidOperationException(load.Error);
            }

            _rallyX[p] = MapUnits / 2;
            _rallyY[p] = MapUnits / 2;
            _aggression[p] = 2500;
        }
    }

    public int BudgetAborts { get; private set; }

    public int ScriptErrors { get; private set; }

    /// <summary>Advances one tick and returns its state hash.</summary>
    public ulong Step(int tick)
    {
        RunScripts(tick);
        Move();
        Fight(tick);

        _hasher.Reset();
        _hasher.Add(tick);
        _hasher.AddSpan<Unit>(_units);
        _hasher.AddSpan<int>(_rallyX).AddSpan<int>(_rallyY).AddSpan<int>(_aggression).AddSpan<int>(_scriptStatus)
            .AddSpan<long>(_scriptCharged).AddSpan<int>(_kills);
        _rng.AppendTo(_hasher);
        return _hasher.GetCurrentHash();
    }

    private void RunScripts(int tick)
    {
        Array.Clear(_alive);
        Array.Clear(_sumX);
        Array.Clear(_sumY);
        foreach (ref readonly Unit u in _units.AsSpan())
        {
            if (u.Hp > 0)
            {
                _alive[u.Owner]++;
                _sumX[u.Owner] += u.X;
                _sumY[u.Owner] += u.Y;
            }
        }

        for (int p = 0; p < Players; p++)
        {
            ScriptResult r = _scripts[p].CallGlobal("onTick", LuaBudget, tick, p, _alive[p], _sumX[p], _sumY[p], _kills[p]);
            _scriptStatus[p] = (int)r.Status;
            _scriptCharged[p] = r.ChargedInstructions;
            if (r.Status == ScriptStatus.BudgetExceeded)
            {
                BudgetAborts++;
                continue; // keep last tick's orders, as the engine would after onScriptError
            }

            if (r.Status != ScriptStatus.Ok || r.Values.Length < 3
                || !TryInt(r.Values[0], out int x) || !TryInt(r.Values[1], out int y) || !TryInt(r.Values[2], out int aggression))
            {
                ScriptErrors++;
                continue;
            }

            _rallyX[p] = Math.Clamp(x, 0, MapUnits - 1);
            _rallyY[p] = Math.Clamp(y, 0, MapUnits - 1);
            _aggression[p] = Math.Clamp(aggression, 0, 10_000);
        }
    }

    // The host boundary: a Lua number becomes an engine int only if it is an exact integer in range.
    private static bool TryInt(LuaValue value, out int result)
    {
        result = 0;
        if (value.Type != LuaValueType.Number)
        {
            return false;
        }

        double d = value.Read<double>();
        if (!(d >= int.MinValue && d <= int.MaxValue) || Math.Floor(d) != d)
        {
            return false;
        }

        result = (int)d;
        return true;
    }

    private void Move()
    {
        Span<Unit> units = _units;
        for (int i = 0; i < units.Length; i++)
        {
            ref Unit u = ref units[i];
            if (u.Hp <= 0)
            {
                // Respawn at the rally point.
                u.Hp = 1000;
                u.X = _rallyX[u.Owner];
                u.Y = _rallyY[u.Owner];
                continue;
            }

            long dx = u.TargetX - u.X;
            long dy = u.TargetY - u.Y;
            long dist = IntMath.ISqrt((dx * dx) + (dy * dy));
            Fixed step = Fixed.FromRaw(u.Speed) * 64; // units per tick
            if (dist <= step.ToInt())
            {
                u.X = u.TargetX;
                u.Y = u.TargetY;
                ref Xoshiro256StarStar rng = ref _rng.Ai(u.Owner);
                u.TargetX = Math.Clamp(_rallyX[u.Owner] + rng.NextInt(-8 * TileUnits, 8 * TileUnits), 0, MapUnits - 1);
                u.TargetY = Math.Clamp(_rallyY[u.Owner] + rng.NextInt(-8 * TileUnits, 8 * TileUnits), 0, MapUnits - 1);
                continue;
            }

            Angle heading = Trig.Atan2(dy, dx);
            u.Facing = heading.Raw;
            u.X = Math.Clamp(u.X + (int)(Trig.Cos(heading) * step).ToInt(), 0, MapUnits - 1);
            u.Y = Math.Clamp(u.Y + (int)(Trig.Sin(heading) * step).ToInt(), 0, MapUnits - 1);
        }
    }

    private void Fight(int tick)
    {
        Span<Unit> units = _units;
        const long Range = 3L * TileUnits;
        for (int i = 0; i < units.Length; i++)
        {
            ref Unit a = ref units[i];
            int j = (int)(((long)i * 7919 + (long)tick * 31) % units.Length);
            ref Unit b = ref units[j];
            if (a.Hp <= 0 || b.Hp <= 0 || a.Owner == b.Owner
                || IntMath.DistanceSquared(a.X, a.Y, b.X, b.Y) > Range * Range)
            {
                continue;
            }

            if (_rng.Combat.NextChance(_aggression[a.Owner]))
            {
                b.Hp -= (int)IntMath.MulBasisPoints(150 + _rng.Combat.NextInt(100), 5000 + _aggression[a.Owner]);
                if (b.Hp <= 0)
                {
                    a.Kills++;
                    _kills[a.Owner]++;
                }
            }
        }
    }
}
