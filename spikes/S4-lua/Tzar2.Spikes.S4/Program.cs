using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Lua;
using Tzar2.Spikes.Lua;

namespace Tzar2.Spikes.S4;

/// <summary>Spike S4 probe: <c>sandbox</c>, <c>budget</c>, <c>bench</c> or (default) all of them.</summary>
public static class Program
{
    private const long Budget = 100_000;
    private static int s_failures;

    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "all";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"S4 Lua sandbox probe | {RuntimeInformation.FrameworkDescription} | {RuntimeInformation.ProcessArchitecture} | " +
            $"AOT={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}"));
        if (mode is "all" or "sandbox")
        {
            Sandbox();
        }

        // The benchmark runs before the budget checks: one budget check deliberately leaves a spinning thread behind.
        if (mode is "all" or "bench")
        {
            Bench(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5);
        }

        if (mode is "all" or "budget")
        {
            BudgetChecks();
        }

        Console.WriteLine(s_failures == 0 ? "RESULT: all checks passed" : $"RESULT: {s_failures} check(s) FAILED");
        return s_failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- sandbox escapes

    private static void Sandbox()
    {
        Console.WriteLine();
        Console.WriteLine("== Sandbox: every probe must be blocked (nil, error or rejection) ==");
        (string Name, string Code)[] probes =
        {
            ("io", "return io == nil"),
            ("os", "return os == nil"),
            ("debug", "return debug == nil"),
            ("package/require/module", "return package == nil and require == nil and module == nil"),
            ("dofile/loadfile", "return dofile == nil and loadfile == nil"),
            ("load/loadstring", "return load == nil and loadstring == nil"),
            ("load via _G concat", "return _G['lo' .. 'ad'] == nil and rawget(_G, 'load') == nil"),
            ("collectgarbage", "return collectgarbage == nil"),
            ("string.dump", "return string.dump == nil and ('').dump == nil"),
            ("string metatable locked", "return getmetatable('') == false"),
            ("setmetatable on string mt", "return not pcall(setmetatable, getmetatable(''), {})"),
            ("math transcendental removed", "return math.sin == nil and math.exp == nil and math.pow == nil and math.sqrt == nil and math.log == nil"),
            ("math.randomseed removed", "return math.randomseed == nil"),
            ("math.random() float blocked", "return not pcall(math.random)"),
            ("tostring(table) blocked", "return not pcall(tostring, {})"),
            ("tostring with __tostring ok", "return tostring(setmetatable({}, {__tostring = function() return 'T' end})) == 'T'"),
            ("string.format %s table blocked", "return not pcall(string.format, '%s', {})"),
            ("string.rep 1e9 blocked", "return not pcall(string.rep, 'x', 1e9)"),
            ("string.sub NaN index blocked", "return not pcall(string.sub, 'abc', 0/0)"),
            ("string.sub 1e20 index blocked", "return not pcall(string.sub, 'abc', 1e20)"),
            ("bit32.lshift 1e20 blocked", "return not pcall(bit32.lshift, 1, 1e20)"),
            ("bit32.band 2^32-1 still ok", "return bit32.band(4294967295, 255) == 255"),
            ("select NaN blocked", "return not pcall(select, 0/0, 1, 2)"),
            ("table.insert NaN pos blocked", "return not pcall(table.insert, {1,2}, 0/0, 3)"),
            ("math.random(1,6) in range", "local ok = true for i = 1, 1000 do local r = math.random(1, 6) ok = ok and r >= 1 and r <= 6 and r == math.floor(r) end return ok"),
            ("fx.isqrt", "return fx.isqrt(1000000) == 1000"),
            ("coroutine.wrap works", "local g = coroutine.wrap(function(a) local b = coroutine.yield(a + 1) return b * 2 end) return g(1) == 2 and g(5) == 10"),
        };
        foreach ((string name, string code) in probes)
        {
            var sandbox = new LuaSandbox(42);
            ScriptResult r = sandbox.DoString(code, "=" + name, Budget);
            bool ok = r.Status == ScriptStatus.Ok && r.Values.Length > 0 && r.Values[0].Type == LuaValueType.Boolean && r.Values[0].Read<bool>();
            Report(name, ok, r);
        }

        // Source policy and loader.
        var s = new LuaSandbox(1);
        ScriptResult pow = s.DoString("return 2 ^ 10", "=pow", Budget);
        Report("'^' rejected at load", pow.Status == ScriptStatus.Error && pow.Error!.Contains("'^'", StringComparison.Ordinal), pow);
        ScriptResult powInString = s.DoString("return '2^10' .. [[^]] -- ^ in comment", "=pow-in-string", Budget);
        Report("'^' inside strings/comments allowed", powInString.Status == ScriptStatus.Ok, powInString);
        ScriptResult bytecode = s.DoString("\u001bLuaR\u0000", "=bytecode", Budget);
        Report("binary chunk rejected (text-only loader)", bytecode.Status == ScriptStatus.Error, bytecode);

        // Deep recursion must end in a catchable error, not a host crash.
        ScriptResult rec = new LuaSandbox(1).DoString("local function f(n) return f(n + 1) + 1 end return f(1)", "=recursion", long.MaxValue / 2);
        Report("deep Lua recursion -> error, host survives", rec.Status == ScriptStatus.Error, rec);
        ScriptResult pcallRec = new LuaSandbox(1, new LuaSandboxOptions { HookInterval = 0 })
            .DoString("local d = 0 local function f() d = d + 1 pcall(f) end f() return d", "=pcall-recursion", 0);
        Report("pcall recursion stops at the deterministic re-entry limit", pcallRec.Status == ScriptStatus.Ok
            && pcallRec.Values[0].Read<double>() == LuaSandbox.MaxReentryDepth + 1, pcallRec,
            pcallRec.Values.Length > 0 ? "depth " + pcallRec.Values[0] : null);
        ScriptResult indexRec = new LuaSandbox(1, new LuaSandboxOptions { HookInterval = 0 })
            .DoString("local d = 0 local t t = setmetatable({}, {__index = function(_, k) d = d + 1 return t[k] end}) pcall(function() return t.x end) return d", "=index-recursion", 0);
        Report("__index recursion (C# stack, not limited by the sandbox) -> host survives", indexRec.Status == ScriptStatus.Ok, indexRec,
            indexRec.Values.Length > 0 ? "depth " + indexRec.Values[0] + " (native-stack dependent)" : null);
    }

    // ---------------------------------------------------------------- budget

    private static void BudgetChecks()
    {
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"== Budget ({Budget} instructions, hook every 1000) =="));
        (string Name, string Code)[] runaway =
        {
            ("while true", "while true do end"),
            ("pcall swallowing", "while true do pcall(function() while true do end end) end"),
            ("xpcall swallowing", "while true do xpcall(function() while true do end end, function(e) return e end) end"),
            ("error() in handler loop", "while true do pcall(error, 'x') end"),
            ("coroutine body", "local co = coroutine.create(function() while true do end end) coroutine.resume(co)"),
            ("coroutine.wrap + pcall", "while true do pcall(coroutine.wrap(function() while true do end end)) end"),
            ("table.sort comparator", "local t = {} for i = 1, 100 do t[i] = i end table.sort(t, function(a, b) while true do end end)"),
            ("__index metamethod", "local t = setmetatable({}, {__index = function() while true do end end}) return t.x"),
            ("gsub callback", "return string.gsub('aaaa', 'a', function() while true do end end)"),
            ("recursion", "local function f() return f() end return f()"),
        };
        foreach ((string name, string code) in runaway)
        {
            ScriptResult r = new LuaSandbox(7).DoString(code, "=" + name, Budget);
            Report(name + " -> aborted", r.Status == ScriptStatus.BudgetExceeded, r);
        }

        // Reuse after abort, per-call budget, and the exact abort point.
        var sandbox = new LuaSandbox(7);
        sandbox.DoString("n = 0 function spin(k) for i = 1, k do n = n + 1 end return n end", "=setup", Budget);
        ScriptResult aborted = sandbox.CallGlobal("spin", Budget, 1e9);
        double abortedAt = sandbox.State.Environment["n"].Read<double>();
        ScriptResult again = sandbox.CallGlobal("spin", Budget, 10);
        Report("state reusable after abort", aborted.Status == ScriptStatus.BudgetExceeded && again.Status == ScriptStatus.Ok, again);

        bool allAborted = true;
        for (int i = 0; i < 5; i++)
        {
            allAborted &= sandbox.CallGlobal("spin", Budget, 1e9).Status == ScriptStatus.BudgetExceeded;
            allAborted &= sandbox.CallGlobal("spin", Budget, 10).Status == ScriptStatus.Ok;
        }

        Report("5 more aborts on the same state", allAborted, again);
        Report("upstream bug: a hook that throws disables later hooks", ThrowingHookDisablesLaterHooks(), again,
            "2nd runaway call with a throwing hook did not stop within 2 s (LuaState.IsInHook stays true)");

        ScriptResult a = sandbox.CallGlobal("spin", Budget, 20_000);
        ScriptResult b = sandbox.CallGlobal("spin", Budget, 20_000);
        Report("budget is per call (2 x 60%)", a.Status == ScriptStatus.Ok && b.Status == ScriptStatus.Ok, b);

        var abortPoints = new HashSet<double>();
        for (int i = 0; i < 50; i++)
        {
            var fresh = new LuaSandbox(7);
            fresh.DoString("n = 0 function spin(k) for i = 1, k do n = n + 1 end return n end", "=setup", Budget);
            fresh.CallGlobal("spin", Budget, 1e9);
            abortPoints.Add(fresh.State.Environment["n"].Read<double>());
        }

        Report("abort point identical over 50 fresh states", abortPoints.Count == 1, aborted,
            string.Create(CultureInfo.InvariantCulture, $"loop iterations before abort = {abortedAt}, charged = {aborted.ChargedInstructions}"));

        // Exact instruction counts (hook every instruction).
        var exact = new LuaSandbox(7, new LuaSandboxOptions { HookInterval = 1 });
        exact.DoString("function spin(k) local x = 0 for i = 1, k do x = x + i end return x end", "=setup", Budget);
        long c10 = exact.CallGlobal("spin", long.MaxValue / 2, 10).ChargedInstructions;
        long c1010 = exact.CallGlobal("spin", long.MaxValue / 2, 1010).ChargedInstructions;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  info: numeric-for body costs {(c1010 - c10) / 1000.0} instructions/iteration (exact count, interval 1)"));
    }

    // Demonstrates the Lua-CSharp 0.5.7 behaviour the sandbox works around: returns true if the second runaway
    // call under a throwing count hook is NOT stopped (the thread is abandoned; it is a background thread).
    private static bool ThrowingHookDisablesLaterHooks()
    {
        bool secondStopped = false;
        var thread = new System.Threading.Thread(() =>
        {
            LuaState state = LuaState.Create();
            state.SetHook(new LuaFunction("throw", (_, _) => throw new OperationCanceledException("budget")), string.Empty, 1000);
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    state.DoStringAsync("while true do end").AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    secondStopped = i == 1;
                }
            }
        })
        { IsBackground = true };
        thread.Start();
        return !thread.Join(TimeSpan.FromSeconds(2)) && !secondStopped;
    }

    // ---------------------------------------------------------------- benchmark

    private const string BenchScript = """
        local function fib(n) if n < 2 then return n end return fib(n - 1) + fib(n - 2) end
        function work()
          local t = {}
          -- t[i], not t[#t + 1]: '#' is a linear scan in Lua-CSharp 0.5.7 (see the length-operator line below).
          for i = 1, 20000 do t[i] = (i * 7) % 13 end
          local s = 0
          for i, v in ipairs(t) do s = s + v end
          local keys = {}
          for i = 1, 500 do keys['k' .. (i % 97)] = i end
          for k, v in pairs(keys) do s = s + v end
          return s + fib(20)
        end
        """;

    private static void Bench(int repetitions)
    {
        Console.WriteLine();
        Console.WriteLine("== Overhead of the count hook (median of " + repetitions.ToString(CultureInfo.InvariantCulture) + " x 20 calls) ==");
        // Count the instructions of one work() call exactly first.
        var counter = new LuaSandbox(1, new LuaSandboxOptions { HookInterval = 1 });
        counter.DoString(BenchScript, "=bench", long.MaxValue / 2);
        long instructions = counter.CallGlobal("work", long.MaxValue / 2).ChargedInstructions;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  one work() call = {instructions} VM instructions"));

        var lengthProbe = new LuaSandbox(1, new LuaSandboxOptions { HookInterval = 0 });
        lengthProbe.DoString("function a(n) local t = {} for i = 1, n do t[#t + 1] = i end end function b(n) local t = {} for i = 1, n do t[i] = i end end", "=len", 0);
        foreach (int n in new[] { 5_000, 20_000 })
        {
            long t0 = Stopwatch.GetTimestamp();
            lengthProbe.CallGlobal("a", 0, n);
            double withLength = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            t0 = Stopwatch.GetTimestamp();
            lengthProbe.CallGlobal("b", 0, n);
            double withIndex = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  length operator: {n} appends via t[#t+1] = {withLength:F2} ms, via t[i] = {withIndex:F2} ms"));
        }

        int[] intervals = { 0, 1, 10, 100, 1000, 10_000, 1_000_000_000 };
        double baseline = 0;
        Console.WriteLine("  | hook interval | ms / call | ns / instr | overhead |");
        Console.WriteLine("  |---|---|---|---|");
        foreach (int interval in intervals)
        {
            var sandbox = new LuaSandbox(1, new LuaSandboxOptions { HookInterval = interval });
            sandbox.DoString(BenchScript, "=bench", long.MaxValue / 2);
            for (int w = 0; w < 10; w++)
            {
                sandbox.CallGlobal("work", long.MaxValue / 2);
            }

            var samples = new List<double>();
            for (int r = 0; r < repetitions; r++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 20; i++)
                {
                    ScriptResult res = sandbox.CallGlobal("work", long.MaxValue / 2);
                    if (res.Status != ScriptStatus.Ok)
                    {
                        throw new InvalidOperationException(res.Error);
                    }
                }

                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / 20);
            }

            samples.Sort();
            double median = samples[samples.Count / 2];
            if (interval == 0)
            {
                baseline = median;
            }

            string label = interval == 0 ? "none (no budget)" : interval == 1_000_000_000 ? "1e9 (~one firing)" : interval.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  | {label} | {median:F3} | {median * 1e6 / instructions:F2} | {(interval == 0 ? "-" : $"{(median / baseline - 1) * 100:+0.0;-0.0}%")} |"));
        }
    }

    private static void Report(string name, bool ok, ScriptResult r, string? info = null)
    {
        if (!ok)
        {
            s_failures++;
        }

        string detail = info ?? (r.Status == ScriptStatus.Ok ? string.Join(", ", r.Values.Select(v => v.ToString())) : $"{r.Status}: {r.Error}");
        if (detail.Length > 110)
        {
            detail = detail[..110] + "...";
        }

        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {name} ({detail})");
    }
}
