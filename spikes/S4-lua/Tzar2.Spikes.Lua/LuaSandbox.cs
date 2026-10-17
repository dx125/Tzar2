using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lua;
using Lua.Runtime;
using Lua.Standard;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;

namespace Tzar2.Spikes.Lua;

/// <summary>Outcome of one budgeted call.</summary>
public enum ScriptStatus
{
    /// <summary>The call returned normally.</summary>
    Ok,

    /// <summary>The call was aborted by the instruction budget (REQ-SCR-06).</summary>
    BudgetExceeded,

    /// <summary>The script raised an error that it did not catch.</summary>
    Error,
}

/// <summary>Result of <see cref="LuaSandbox.Call"/>.</summary>
public readonly record struct ScriptResult(ScriptStatus Status, LuaValue[] Values, string? Error, long ChargedInstructions);

/// <summary>Options of a sandbox.</summary>
public sealed record LuaSandboxOptions
{
    /// <summary>
    /// The count hook fires every <c>HookInterval</c> VM instructions and charges that many against the budget.
    /// The budget is therefore enforced with this granularity; 0 disables the budget (overhead baseline).
    /// </summary>
    public int HookInterval { get; init; } = 1000;

    /// <summary>Longest string that <c>string.rep</c> may produce.</summary>
    public int MaxRepLength { get; init; } = 1 << 16;
}

/// <summary>
/// One sandboxed Lua 5.2 state (spike S4). Only the deterministic subset of the standard library is exposed,
/// integer arguments of library functions are validated before they reach Lua-CSharp's double-to-int casts
/// (which differ between x64 and ARM64 on .NET 8 for NaN and out-of-range values, see ADR 0007), and every call
/// runs under an instruction budget enforced by a count hook.
/// </summary>
public sealed class LuaSandbox
{
    private const double Int32Max = int.MaxValue;
    private const double UInt32Span = 4294967296.0;

    /// <summary>Deterministic limit for pcall/xpcall/sort/gsub/resume/... nesting (see <see cref="Wrap"/>).</summary>
    public const int MaxReentryDepth = 128;

    private readonly LuaState _state;
    private readonly LuaFunction? _hook;
    private readonly int _hookInterval;
    private readonly LuaSandboxOptions _options;
    private readonly List<string> _log = new();
    private int _reentryDepth;
    private CancellationTokenSource _cts = new();
    private Xoshiro256StarStar _rng;
    private long _remaining;
    private long _charged;
    private bool _exhausted;

    /// <summary>Creates a sandbox whose <c>math.random</c> draws from an engine RNG seeded with <paramref name="seed"/>.</summary>
    public LuaSandbox(ulong seed, LuaSandboxOptions? options = null)
    {
        _options = options ?? new LuaSandboxOptions();
        _hookInterval = _options.HookInterval;
        _rng = Xoshiro256StarStar.FromSeed(seed);
        _state = LuaState.Create();
        if (_hookInterval > 0)
        {
            _hook = new LuaFunction("budget", OnHook);
        }

        _state.OpenBasicLibrary();
        _state.OpenStringLibrary();
        _state.OpenTableLibrary();
        _state.OpenMathLibrary();
        _state.OpenBitwiseLibrary();
        _state.OpenCoroutineLibrary();
        Harden();
    }

    /// <summary>The underlying state (tests and benchmarks only).</summary>
    public LuaState State => _state;

    /// <summary>Lines passed to <c>print</c>.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Total count-hook firings since creation (overhead accounting).</summary>
    public long HookFirings { get; private set; }

    /// <summary>Compiles and runs a chunk under a budget. The source must pass <see cref="SourcePolicy"/>.</summary>
    public ScriptResult DoString(string source, string chunkName, long budget)
    {
        string? violation = SourcePolicy.Check(source);
        if (violation is not null)
        {
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), $"{chunkName}: {violation}", 0);
        }

        LuaClosure closure;
        try
        {
            // Text only: the string overload never accepts precompiled bytecode.
            closure = _state.Load(source.AsSpan(), chunkName);
        }
        catch (LuaParseException ex)
        {
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), ex.Message, 0);
        }
        catch (LuaCompileException ex)
        {
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), ex.Message, 0);
        }

        return Call(closure, budget);
    }

    /// <summary>Calls the global function <paramref name="name"/> under a budget.</summary>
    public ScriptResult CallGlobal(string name, long budget, params LuaValue[] args)
    {
        LuaValue function = _state.Environment[name];
        if (function.Type != LuaValueType.Function)
        {
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), $"global '{name}' is not a function", 0);
        }

        return Call(function, budget, args);
    }

    /// <summary>
    /// Calls <paramref name="function"/> synchronously with a fresh budget of <paramref name="budget"/> instructions.
    /// The call is aborted at the first hook point at which more than the budget has been charged.
    /// </summary>
    public ScriptResult Call(LuaValue function, long budget, params LuaValue[] args)
    {
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        _remaining = budget;
        _reentryDepth = 0;
        _charged = 0;
        _exhausted = false;
        if (_hook is not null)
        {
            // Re-arming resets the main thread's countdown, so a call never inherits the previous call's residue.
            _state.SetHook(_hook, string.Empty, _hookInterval);
        }

        int top = _state.Stack.Count;
        try
        {
            ValueTask<LuaValue[]> pending = _state.CallAsync(function, args, _cts.Token);
            if (!pending.IsCompleted)
            {
                // Nothing in the sandbox is asynchronous; a pending task would mean a host bug.
                throw new InvalidOperationException("Lua call did not complete synchronously");
            }

            return new ScriptResult(ScriptStatus.Ok, pending.Result, null, _charged);
        }
        catch (OperationCanceledException) when (_exhausted)
        {
            return new ScriptResult(ScriptStatus.BudgetExceeded, Array.Empty<LuaValue>(), "instruction budget exceeded", _charged);
        }
        catch (LuaRuntimeException ex)
        {
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), ex.Message, _charged);
        }
        catch (ArithmeticException ex)
        {
            // Checked Fixed arithmetic inside fx.* (overflow, division by zero) - deterministic on every peer.
            return new ScriptResult(ScriptStatus.Error, Array.Empty<LuaValue>(), ex.Message, _charged);
        }
        finally
        {
            if (_state.Stack.Count > top)
            {
                _state.Stack.PopUntil(top);
            }
        }
    }

    private ValueTask<int> OnHook(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        HookFirings++;
        _charged += _hookInterval;
        _remaining -= _hookInterval;
        if (_remaining < 0 || _exhausted)
        {
            // Do NOT throw here: Lua-CSharp 0.5.7 sets LuaState.IsInHook around the count hook without try/finally,
            // so an exception thrown by the hook leaves IsInHook = true and silently disables every later hook on
            // that thread (the next runaway call would never stop). Cancelling instead is safe: the VM checks the
            // token right after the hook returns and throws LuaCanceledException, which pcall rethrows and xpcall
            // rethrows because the token is cancelled.
            _exhausted = true;
            _cts.Cancel();
        }

        return new ValueTask<int>(context.Return());
    }

    private void Harden()
    {
        LuaTable env = _state.Environment;

        // Lock the string metatable before getmetatable is the only way to reach it.
        _state.DoStringAsync("getmetatable('').__metatable = false", "=sandbox").AsTask().GetAwaiter().GetResult();

        // Basic library: no code loading, no GC control/introspection, no direct console output.
        foreach (string name in new[] { "dofile", "loadfile", "load", "loadstring", "collectgarbage", "require", "module" })
        {
            env[name] = LuaValue.Nil;
        }

        env["print"] = new LuaFunction("print", Print);
        env["tostring"] = Wrap("tostring", env["tostring"], ValidateToString, reentrant: true);
        foreach (string name in new[] { "pcall", "xpcall", "pairs", "ipairs" })
        {
            env[name] = Wrap(name, env[name], null, reentrant: true);
        }

        env["select"] = Wrap("select", env["select"], (ctx, name) =>
        {
            if (ctx.ArgumentCount > 0 && ctx.Arguments[0].Type == LuaValueType.Number)
            {
                RequireInteger(ctx, name, 0, -Int32Max, Int32Max);
            }
        });

        // string: no dump; integer arguments validated; rep bounded; format takes plain values only.
        LuaTable str = env["string"].Read<LuaTable>();
        str["dump"] = LuaValue.Nil;
        foreach (string name in new[] { "byte", "char", "sub", "find", "match", "gmatch" })
        {
            str[name] = Wrap(name, str[name], ValidateIntegerArgs);
        }

        str["gsub"] = Wrap("gsub", str["gsub"], ValidateIntegerArgs, reentrant: true);

        str["rep"] = Wrap("rep", str["rep"], ValidateRep);
        str["format"] = Wrap("format", str["format"], ValidatePlainArgs);

        // table: integer arguments validated.
        LuaTable table = env["table"].Read<LuaTable>();
        foreach (string name in new[] { "insert", "remove", "concat", "unpack" })
        {
            table[name] = Wrap(name, table[name], ValidateIntegerArgs);
        }

        table["sort"] = Wrap("sort", table["sort"], null, reentrant: true);

        // math: deterministic subset only (REQ-SCR-04); random draws from the engine RNG, integers only.
        LuaTable fullMath = env["math"].Read<LuaTable>();
        var math = new LuaTable();
        foreach (string name in new[] { "abs", "ceil", "floor", "max", "min", "fmod" })
        {
            math[name] = fullMath[name];
        }

        math["huge"] = double.PositiveInfinity;
        math["random"] = new LuaFunction("random", Random);
        env["math"] = math;
        _state.LoadedModules["math"] = math;

        // bit32: values are reduced mod 2^32 safely, but shift/field arguments hit (int) casts.
        LuaTable bit32 = env["bit32"].Read<LuaTable>();
        var bitNames = new List<string>();
        foreach (KeyValuePair<LuaValue, LuaValue> entry in bit32)
        {
            bitNames.Add(entry.Key.Read<string>());
        }

        bitNames.Sort(StringComparer.Ordinal);
        foreach (string name in bitNames)
        {
            bit32[name] = Wrap(name, bit32[name], ValidateBit32Args);
        }

        // coroutine: threads do not inherit the hook in Lua-CSharp, so create/wrap must arm it themselves.
        LuaTable coroutine = env["coroutine"].Read<LuaTable>();
        coroutine["create"] = new LuaFunction("create", CreateCoroutine);
        coroutine["resume"] = Wrap("resume", coroutine["resume"], null, reentrant: true);
        _state.DoStringAsync(
            """
            local create, resume = coroutine.create, coroutine.resume
            local error, select, unpack = error, select, table.unpack
            coroutine.wrap = function(f)
              local co = create(f)
              return function(...)
                local r = table.pack(resume(co, ...))
                if not r[1] then error(r[2], 2) end
                return unpack(r, 2, r.n)
              end
            end
            """,
            "=sandbox").AsTask().GetAwaiter().GetResult();

        // fx: the engine's fixed-point math (Q47.16 raw values that stay below 2^53 cross as Lua numbers).
        var fx = new LuaTable();
        fx["one"] = (double)Fixed.OneRaw;
        fx["sin"] = new LuaFunction("sin", (ctx, ct) => new(ctx.Return((double)Trig.Sin(Angle.FromRaw(ArgInt(ctx, 0))).Raw)));
        fx["cos"] = new LuaFunction("cos", (ctx, ct) => new(ctx.Return((double)Trig.Cos(Angle.FromRaw(ArgInt(ctx, 0))).Raw)));
        fx["atan2"] = new LuaFunction("atan2", (ctx, ct) => new(ctx.Return((double)Trig.Atan2(ArgLong(ctx, 0), ArgLong(ctx, 1)).Raw)));
        fx["isqrt"] = new LuaFunction("isqrt", (ctx, ct) => new(ctx.Return((double)IntMath.ISqrt(ArgLong(ctx, 0)))));
        fx["mul"] = new LuaFunction("mul", (ctx, ct) => new(ctx.Return(ToLua(ctx, (Fixed.FromRaw(ArgLong(ctx, 0)) * Fixed.FromRaw(ArgLong(ctx, 1))).Raw))));
        fx["div"] = new LuaFunction("div", (ctx, ct) => new(ctx.Return(ToLua(ctx, Div(ctx, ArgLong(ctx, 0), ArgLong(ctx, 1), fixedPoint: true)))));
        fx["idiv"] = new LuaFunction("idiv", (ctx, ct) => new(ctx.Return(ToLua(ctx, Div(ctx, ArgLong(ctx, 0), ArgLong(ctx, 1), fixedPoint: false)))));
        env["fx"] = fx;
    }

    private ValueTask<int> Print(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var parts = new string[context.ArgumentCount];
        for (int i = 0; i < parts.Length; i++)
        {
            LuaValue v = context.Arguments[i];
            parts[i] = IsPlain(v.Type) ? v.ToString() : v.TypeToString();
        }

        _log.Add(string.Join('\t', parts));
        return new ValueTask<int>(context.Return());
    }

    private ValueTask<int> Random(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        // random(m) -> [1, m]; random(m, n) -> [m, n]; random() (a float) is not offered.
        if (context.ArgumentCount == 0)
        {
            throw new LuaRuntimeException(context.State, "math.random() without arguments is not available (floats); use math.random(m[, n])");
        }

        long lo = context.ArgumentCount >= 2 ? ArgInt(context, 0) : 1;
        long hi = context.ArgumentCount >= 2 ? ArgInt(context, 1) : ArgInt(context, 0);
        if (hi < lo || hi - lo >= int.MaxValue)
        {
            throw new LuaRuntimeException(context.State, "bad argument to 'random' (interval is empty or too large)");
        }

        return new ValueTask<int>(context.Return((double)(lo + _rng.NextInt((int)(hi - lo + 1)))));
    }

    private ValueTask<int> CreateCoroutine(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        LuaFunction function = context.GetArgument<LuaFunction>(0);
        LuaState thread = context.State.CreateCoroutine(function, isProtectedMode: true);
        if (_hook is not null)
        {
            thread.SetHook(_hook, string.Empty, _hookInterval);
        }

        return new ValueTask<int>(context.Return(thread));
    }

    private LuaFunction Wrap(string name, LuaValue original, Action<LuaFunctionExecutionContext, string>? validate, bool reentrant = false)
    {
        LuaFunction inner = original.Read<LuaFunction>();
        return new LuaFunction(name, (context, cancellationToken) =>
        {
            validate?.Invoke(context, name);
            LuaValue[] args = context.Arguments.ToArray();
            if (!reentrant)
            {
                return Forward(context, inner, args, cancellationToken);
            }

            // Functions that call back into Lua recurse on the C# stack. Lua-CSharp stops that recursion with
            // RuntimeHelpers.TryEnsureSufficientExecutionStack, i.e. at a depth that depends on the native stack
            // (platform, JIT, runtime version). A host-side counter makes the limit deterministic.
            if (_reentryDepth >= MaxReentryDepth)
            {
                throw new LuaRuntimeException(context.State, $"'{name}': nesting deeper than {MaxReentryDepth} levels");
            }

            return ForwardCounted(context, inner, args, cancellationToken);
        });
    }

    private static async ValueTask<int> Forward(LuaFunctionExecutionContext context, LuaFunction inner, LuaValue[] args, CancellationToken cancellationToken)
    {
        LuaValue[] results = await context.State.CallAsync(inner, args, cancellationToken).ConfigureAwait(false);
        return context.Return(results);
    }

    private async ValueTask<int> ForwardCounted(LuaFunctionExecutionContext context, LuaFunction inner, LuaValue[] args, CancellationToken cancellationToken)
    {
        _reentryDepth++;
        try
        {
            return await Forward(context, inner, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reentryDepth--;
        }
    }

    private static void ValidateIntegerArgs(LuaFunctionExecutionContext context, string name)
    {
        for (int i = 0; i < context.ArgumentCount; i++)
        {
            if (context.Arguments[i].Type == LuaValueType.Number)
            {
                RequireInteger(context, name, i, -Int32Max, Int32Max);
            }
        }
    }

    private void ValidateRep(LuaFunctionExecutionContext context, string name)
    {
        ValidateIntegerArgs(context, name);
        string s = context.GetArgument<string>(0);
        double n = context.GetArgument<double>(1);
        string sep = context.ArgumentCount > 2 ? context.GetArgument<string>(2) : string.Empty;
        if (n > 0 && ((s.Length + (double)sep.Length) * n) > _options.MaxRepLength)
        {
            throw new LuaRuntimeException(context.State, $"bad argument #2 to 'rep' (result longer than {_options.MaxRepLength})");
        }
    }

    private static void ValidateBit32Args(LuaFunctionExecutionContext context, string name)
    {
        // Values: any integer within +-2^32 (reduced mod 2^32 by IEEERemainder, exact).
        // Displacements, fields and widths go through (int) casts, so they must fit an int.
        int firstCountArg = name switch
        {
            "arshift" or "lshift" or "rshift" or "lrotate" or "rrotate" => 1,
            "extract" => 1,
            "replace" => 2,
            _ => int.MaxValue,
        };
        for (int i = 0; i < context.ArgumentCount; i++)
        {
            if (context.Arguments[i].Type == LuaValueType.Number)
            {
                double limit = i >= firstCountArg ? Int32Max : UInt32Span;
                RequireInteger(context, name, i, -limit, limit);
            }
        }
    }

    private static void ValidatePlainArgs(LuaFunctionExecutionContext context, string name)
    {
        for (int i = 0; i < context.ArgumentCount; i++)
        {
            if (!IsPlain(context.Arguments[i].Type))
            {
                throw new LuaRuntimeException(context.State, $"bad argument #{i + 1} to '{name}' (only nil, boolean, number and string can be formatted)");
            }
        }
    }

    private static void ValidateToString(LuaFunctionExecutionContext context, string name)
    {
        // Reference values print their RuntimeHelpers hash code ("table: 12345678") - different in every process.
        LuaValue v = context.GetArgument(0);
        if (!IsPlain(v.Type) && !(v.Type == LuaValueType.Table && v.Read<LuaTable>().Metatable?["__tostring"].Type == LuaValueType.Function))
        {
            throw new LuaRuntimeException(context.State, "tostring of a table/function/thread is not deterministic; give the table a __tostring");
        }
    }

    private static bool IsPlain(LuaValueType type) =>
        type is LuaValueType.Nil or LuaValueType.Boolean or LuaValueType.Number or LuaValueType.String;

    private static void RequireInteger(LuaFunctionExecutionContext context, string name, int index, double min, double max)
    {
        double d = context.Arguments[index].Read<double>();
        if (!(d >= min && d <= max) || Math.Floor(d) != d)
        {
            throw new LuaRuntimeException(context.State, $"bad argument #{index + 1} to '{name}' (integer in [{min}, {max}] expected)");
        }
    }

    private const double MaxSafeInteger = 9007199254740991.0; // 2^53 - 1

    private static long ArgLong(LuaFunctionExecutionContext context, int index)
    {
        double d = context.GetArgument<double>(index);
        if (!(d >= -MaxSafeInteger && d <= MaxSafeInteger) || Math.Floor(d) != d)
        {
            throw new LuaRuntimeException(context.State, $"bad argument #{index + 1} (integer in +-(2^53-1) expected)");
        }

        return (long)d;
    }

    private static int ArgInt(LuaFunctionExecutionContext context, int index)
    {
        long v = ArgLong(context, index);
        if (v is < int.MinValue or > int.MaxValue)
        {
            throw new LuaRuntimeException(context.State, $"bad argument #{index + 1} (int32 expected)");
        }

        return (int)v;
    }

    private static double ToLua(LuaFunctionExecutionContext context, long value)
    {
        if (value is < -(long)MaxSafeInteger or > (long)MaxSafeInteger)
        {
            throw new LuaRuntimeException(context.State, "result exceeds +-(2^53-1)");
        }

        return value;
    }

    private static long Div(LuaFunctionExecutionContext context, long a, long b, bool fixedPoint)
    {
        if (b == 0)
        {
            throw new LuaRuntimeException(context.State, "division by zero");
        }

        return fixedPoint ? (Fixed.FromRaw(a) / Fixed.FromRaw(b)).Raw : a / b;
    }
}
