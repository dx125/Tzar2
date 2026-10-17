using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lua;
using Lua.Standard;
using Tzar2.Core.Hashing;
using Tzar2.Spikes.Lua;

namespace Tzar2.Spikes.S2;

/// <summary>
/// Targeted probes of the places where Lua-CSharp (doubles, the C library, double-to-int casts, the native stack,
/// object hash codes) could differ between platforms. Most run in an unsandboxed state with the full standard library,
/// so they show what the sandbox has to remove; the last ones run inside <see cref="LuaSandbox"/>.
/// Every value is serialized exactly (numbers as IEEE bit patterns), so any divergence is visible.
/// </summary>
internal static class Probes
{
    private const string Prelude = """
        local r, n = {}, 0
        local function add(v) n = n + 1 if v == nil then r[n] = "<nil>" else r[n] = v end end
        local function try(f, ...) local ok, v = pcall(f, ...) add(ok) add(v) end
        local function lcg(s) return (s * 1103515245 + 12345) % 2147483648 end
        """;

    private const string Epilogue = "\nreturn r, n";

    public static readonly (string Name, bool Sandboxed, string Code)[] All =
    {
        ("A ieee-basic (+ - * / % sqrt floor, subnormal, inf, -0)", false, """
            local s = 1
            for i = 1, 3000 do
              s = lcg(s)
              local a = s / 2147483648 * 1000 - 500
              s = lcg(s)
              local b = s / 2147483648 * 7 + 0.001
              add(a + b) add(a - b) add(a * b) add(a / b) add(a % b) add(math.sqrt(math.abs(a)))
              add(math.floor(a / b)) add(math.fmod(a, b)) add(math.ceil(a * 3.3))
            end
            add(1e-300 / 1e10) add(5e-324 / 2) add(1e308 * 10) add(-1e308 * 10) add(-0.0) add(0 * -1)
            add(1 / 3 * 3) add(0.1 + 0.2) add(9007199254740993 + 0) add(2147483647 * 2147483647)
            """),
        ("B libm (sin cos tan asin acos atan atan2 exp log sinh cosh tanh pow ^)", false, """
            local s = 7
            for i = 1, 1000 do
              s = lcg(s)
              local x = s / 2147483648 * 20 - 10
              add(math.sin(x)) add(math.cos(x)) add(math.tan(x)) add(math.asin(x / 10)) add(math.acos(x / 10))
              add(math.atan(x)) add(math.atan2(x, 3.5 - x)) add(math.exp(x)) add(math.log(math.abs(x) + 1e-9))
              add(math.sinh(x)) add(math.cosh(x)) add(math.tanh(x)) add(math.pow(math.abs(x), 0.37)) add(math.abs(x) ^ 1.7)
              add(math.log(math.abs(x) + 1, 10))
            end
            """),
        // C runs host-driven (RunCastProbes): some of these calls never return on one architecture.
        ("C double->int casts in the stdlib (NaN, +-1e20, 2^31..2^32), 2 s timeout per call", false, ""),
        ("D NaN bit patterns and special values", false, """
            local nan = 0 / 0
            add(nan) add(-nan) add(math.huge - math.huge) add(math.sqrt(-1)) add(0 * math.huge) add(math.fmod(1, 0))
            add(nan ~= nan) add(tostring(nan)) add(tostring(-nan)) add(tostring(-0.0)) add(1 / -0.0) add(tostring(1 / 0))
            add(math.floor(nan)) add(math.max(nan, 1)) add(math.min(1, nan)) add(math.huge % 2) add(5 % math.huge) add(-5 % math.huge)
            """),
        ("E number formatting and parsing", false, """
            for _, v in ipairs({ 1 / 3, 1e100, 2 ^ 53 + 1, 0.1, 123456789012345678, 1e-7, -1.5e-310, 3.14159265358979 }) do
              add(tostring(v)) add(string.format("%.17g", v)) add(string.format("%5.2f", v)) add(string.format("%e", v))
              add(string.format("%g", v)) add(v .. "")
            end
            for _, s in ipairs({ "0x1p4", "1e-310", "  12  ", "1e400", "0x7fffffffffffffff", "0.1", "1e", ".5", "5.", "0x.8" }) do
              add(tonumber(s))
            end
            """),
        ("F recursion limits (Lua stack, C# stack via pcall/__index, parser nesting)", false, """
            local d = 0
            local function f() d = d + 1 return f() + 1 end
            add(pcall(f)) add(d)
            d = 0
            local function g() d = d + 1 pcall(g) end
            g() add(d)
            d = 0
            local t t = setmetatable({}, { __index = function(_, k) d = d + 1 return t[k] end })
            add(pcall(function() return t.x end)) add(d)
            local lo, hi = 1, 4000
            while lo < hi do
              local mid = math.floor((lo + hi + 1) / 2)
              if load("return " .. string.rep("(", mid) .. "1" .. string.rep(")", mid)) then lo = mid else hi = mid - 1 end
            end
            add(lo)
            """),
        ("G pairs() order (string/number/boolean/table keys, inserts and deletes)", false, """
            local t, tk = {}, {}
            for i = 1, 200 do t["key" .. ((i * 37) % 211)] = i end
            for i = 1, 200, 3 do t["key" .. ((i * 37) % 211)] = nil end
            for i = 1, 60 do t["new" .. i] = i end
            t[1e6] = 1 t[-5] = 2 t[0.5] = 3 t[true] = 4 t[3] = 5 t[1] = 6
            for i = 1, 20 do local k = {} tk[k] = i t[k] = i end
            for k, v in pairs(t) do
              if type(k) == "table" then add("table#" .. tk[k]) else add(k) end
              add(v)
            end
            local s = {} for i = 1, 50 do s[#s + 1] = "w" .. ((i * 7919) % 101) end
            table.sort(s) for _, v in ipairs(s) do add(v) end
            """),
        ("H reference tostring (expected to differ per process)", false, """
            add(tostring({})) add(tostring(print)) add(string.format("%s", {})) add(tostring(coroutine.create(print)))
            """),
        ("I unseeded and seeded math.random (System.Random)", false, """
            local u = {} for i = 1, 5 do add(math.random(1, 1000000)) end
            math.randomseed(42) for i = 1, 20 do add(math.random(1, 1000000)) end add(math.random())
            """),
        ("J sandbox: budget abort point, re-entry limit, fx.*, engine RNG", true, """
            for i = 1, 50 do add(math.random(1, 1000000)) end
            for a = 0, 65535, 997 do add(fx.sin(a)) add(fx.cos(a)) add(fx.atan2(a - 30000, 12345)) end
            add(fx.isqrt(123456789))
            """),
    };

    /// <summary>Runs every probe; returns (name, hash, serialized values).</summary>
    public static List<(string Name, ulong Hash, List<string> Values)> Run()
    {
        var results = new List<(string, ulong, List<string>)>();
        foreach ((string name, bool sandboxed, string code) in All)
        {
            if (code.Length == 0)
            {
                List<string> casts = RunCastProbes();
                results.Add((name, HashValues(casts), casts));
                continue;
            }

            string source = Prelude + "\n" + code + Epilogue;
            LuaValue[] values;
            if (sandboxed)
            {
                values = RunSandboxed(new LuaSandbox(99), source);
            }
            else
            {
                LuaState state = LuaState.Create();
                state.OpenStandardLibraries();
                values = state.DoStringAsync(source, "=" + name).AsTask().GetAwaiter().GetResult();
            }

            var list = Serialize(values);
            if (sandboxed)
            {
                list.AddRange(SandboxBudgetProbe());
            }

            results.Add((name, HashValues(list), list));
        }

        return results;
    }

    private static ulong HashValues(List<string> values)
    {
        var hasher = new StateHasher();
        foreach (string v in values)
        {
            hasher.Add(Encoding.UTF8.GetBytes(v)).Add((byte)0);
        }

        return hasher.GetCurrentHash();
    }

    private static readonly string[] CastValues =
    {
        "0/0", "1e20", "-1e20", "2147483648", "3000000000", "4294967301", "-2147483649", "2.5", "-0.0",
    };

    private static readonly string[] CastCalls =
    {
        "string.sub('abcdefgh', V)", "string.sub('abcdefgh', 2, V)", "string.byte('abcdefgh', V)",
        "string.rep('ab', V)", "bit32.lshift(1, V)", "bit32.rshift(4294967295, V)", "bit32.arshift(-8, V)",
        "bit32.lrotate(255, V)", "bit32.extract(255, V, 3)", "bit32.extract(255, 0, V)", "bit32.band(V)",
        "select(V, 'a', 'b', 'c')", "string.char(V)", "string.format('%d', V)", "string.format('%x', V)",
        "string.format('%c', V)", "table.unpack({ 1, 2, 3 }, V, 2)", "table.concat({ 'a', 'b', 'c' }, ',', 1, V)",
        "(function() local t = { 1, 2, 3 } table.insert(t, V, 9) return #t end)()",
    };

    // Each call gets a fresh state on its own thread; a call that does not finish in 2 s is recorded as a timeout
    // and its thread abandoned (background thread, ends with the process).
    private static List<string> RunCastProbes()
    {
        var list = new List<string>();
        foreach (string v in CastValues)
        {
            foreach (string call in CastCalls)
            {
                string code = "local ok, r = pcall(function() return " + call.Replace("V", v, StringComparison.Ordinal) + " end) return ok, r";
                string outcome = "timeout";
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        LuaState state = LuaState.Create();
                        state.OpenStandardLibraries();
                        LuaValue[] r = state.DoStringAsync(code, "=cast").AsTask().GetAwaiter().GetResult();
                        outcome = Format(r[0]) + " " + (r.Length > 1 ? Format(r[1]) : "-");
                    }
                    catch (Exception ex)
                    {
                        outcome = "host-exception " + ex.GetType().Name;
                    }
                }, 16 * 1024 * 1024)
                { IsBackground = true };
                thread.Start();
                bool finished = thread.Join(TimeSpan.FromSeconds(2));
                list.Add(call.Replace("V", v, StringComparison.Ordinal) + " => " + (finished ? outcome : "timeout"));
            }
        }

        return list;
    }

    private static LuaValue[] RunSandboxed(LuaSandbox sandbox, string source)
    {
        ScriptResult r = sandbox.DoString(source, "=probe", 10_000_000);
        if (r.Status != ScriptStatus.Ok)
        {
            throw new InvalidOperationException(r.Error);
        }

        return r.Values;
    }

    private static IEnumerable<string> SandboxBudgetProbe()
    {
        var sandbox = new LuaSandbox(5);
        sandbox.DoString("n = 0 function spin() while true do n = n + 1 end end " +
            "function nest() local d = 0 local function g() d = d + 1 pcall(g) end g() return d end", "=setup", 1_000_000);
        foreach (long budget in new long[] { 1_000, 12_345, 100_000, 1_000_000 })
        {
            sandbox.State.Environment["n"] = 0;
            ScriptResult r = sandbox.CallGlobal("spin", budget);
            yield return FormattableString.Invariant($"budget {budget}: {r.Status} charged={r.ChargedInstructions} n={sandbox.State.Environment["n"]}");
        }

        ScriptResult nest = sandbox.CallGlobal("nest", 10_000_000);
        yield return FormattableString.Invariant($"nest: {nest.Status} depth={(nest.Values.Length > 0 ? nest.Values[0].ToString() : "-")}");
    }

    private static List<string> Serialize(LuaValue[] values)
    {
        var list = new List<string>();
        LuaTable table = values[0].Read<LuaTable>();
        int count = (int)values[1].Read<double>();
        for (int i = 1; i <= count; i++)
        {
            list.Add(Format(table[i]));
        }

        return list;
    }

    public static string Format(LuaValue v) => v.Type switch
    {
        LuaValueType.Number => string.Create(CultureInfo.InvariantCulture,
            $"n:{BitConverter.DoubleToInt64Bits(v.Read<double>()):X16} {v.Read<double>():R}"),
        LuaValueType.String => "s:" + v.Read<string>(),
        LuaValueType.Boolean => v.Read<bool>() ? "b:true" : "b:false",
        _ => v.TypeToString(),
    };
}
