using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.Sim;

namespace Tzar2.AI.Runtime;

/// <summary>
/// Executes script passes of one AI player (the VM of ADR 0050): integer-only, deterministic, and budgeted in
/// instructions (a rule visit, a condition and an action cost one each). A pass walks the flat rule array top to
/// bottom; it may span several ticks when the budget runs out, and the goals become the executors' goals only when the
/// pass completes.
/// </summary>
/// <remarks>
/// Semantics (Appendix D.4; every point not in the data is UNVERIFIED, docs/mechanics/ai-placeholders.md):
/// <list type="bullet">
/// <item>A rule is <b>active</b> when all its conditions hold, evaluated left to right with short-circuit (a
/// <c>random</c> after a false condition draws nothing).</item>
/// <item>An active rule runs its actions in source order: <c>trigger</c> sets/clears flags at once (later rules of the
/// same pass see them), <c>forget</c> disables rules, <c>say</c> speaks when the rule becomes active (not again while it
/// stays active), and each <c>have</c> goal is checked and, unless met, collected.</item>
/// <item>A <c>critical</c> rule that is not active, or whose goals are not all met, skips to the end of its block.</item>
/// <item>Goals are maintained, not one-shot: they are re-collected every pass, so losses are replaced.</item>
/// </list>
/// </remarks>
internal static class AiInterpreter
{
    public static int Step(World world, AiCensus census, AiBinding binding, AiPlayerState state, AiOptions options, List<AiChatMessage> chat, int budget)
    {
        AiProgram program = binding.Program;
        ImmutableArray<AiRuleCode> rules = program.Rules;
        int tick = world.Tick.Value;
        if (!state.InPass)
        {
            if (tick < state.NextPassTick)
            {
                return 0;
            }

            state.InPass = true;
            state.Pc = 0;
            state.UnmetResources = 0;
            state.LockedResources = 0;
            state.Pending.Clear();
            state.NextPassTick = tick + options.ThinkIntervalTicks;
        }

        int used = 0;
        while (state.Pc < rules.Length && used < budget)
        {
            int pc = state.Pc;
            AiRuleCode rule = rules[pc];
            used++;
            if (AiPlayerState.Get(state.Forgotten, pc))
            {
                AiPlayerState.Set(state.Latched, pc, false);
                state.Pc = pc + 1;
                continue;
            }

            bool active = true;
            for (int c = 0; c < rule.ConditionCount && active; c++)
            {
                AiConditionCode condition = program.Conditions[rule.FirstCondition + c];
                used += condition.Op == AiConditionOp.Extern ? program.ExternCosts[condition.Operand] : 1;
                active = Evaluate(world, census, binding, state, options, condition);
            }

            bool met = true;
            if (active)
            {
                bool wasActive = AiPlayerState.Get(state.Latched, pc);
                for (int a = 0; a < rule.ActionCount; a++)
                {
                    AiActionCode action = program.Actions[rule.FirstAction + a];
                    used += action.Op == AiActionOp.Forget ? 1 + program.RulesByName[action.Operand].Length : 1;
                    met &= Execute(world, census, binding, state, options, chat, action, pc, wasActive);
                }
            }

            AiPlayerState.Set(state.Latched, pc, active);
            if (rule.Critical && !(active && met))
            {
                AiPlayerState.ClearRange(state.Latched, pc + 1, rule.BlockEnd);
                state.Pc = rule.BlockEnd;
            }
            else
            {
                state.Pc = pc + 1;
            }
        }

        if (state.Pc >= rules.Length)
        {
            state.InPass = false;
            state.PassCount++;
            state.Current.Clear();
            state.Current.AddRange(state.Pending);
            state.Pending.Clear();
        }

        return used;
    }

    private static bool Evaluate(World world, AiCensus census, AiBinding binding, AiPlayerState state, AiOptions options, AiConditionCode c)
    {
        byte slot = state.Settings.Slot;
        bool result;
        switch (c.Op)
        {
            case AiConditionOp.Count:
            case AiConditionOp.EnemyCount:
                result = c.Operand >= 0 && CountHolds(world, census, binding, slot, options, c);
                break;

            case AiConditionOp.Flag:
                result = c.Operand >= 0 && AiPlayerState.Get(state.Flags, c.Operand);
                break;

            case AiConditionOp.Random:
                result = c.Number > 0 && (c.Number >= 100 || world.Rng.Ai(slot).NextInt(100) < c.Number);
                break;

            case AiConditionOp.Time:
                result = (long)world.Tick.Value * world.Config.TickLengthMilliseconds >= c.Number;
                break;

            case AiConditionOp.Difficulty:
                result = c.Operand >= 0 && c.Operand == (int)state.Settings.Difficulty;
                break;

            case AiConditionOp.Civilization:
                result = binding.CivilizationMatches[c.Operand];
                break;

            case AiConditionOp.Extern:
                result = binding.Program.Externs[c.Operand] is { } e && AiExternEvaluator.Evaluate(world, slot, e, 0) != 0;
                break;

            default:
                result = false;
                break;
        }

        return result != c.Negated;
    }

    private static bool CountHolds(World world, AiCensus census, AiBinding binding, byte slot, AiOptions options, AiConditionCode c)
    {
        bool enemy = c.Op == AiConditionOp.EnemyCount;
        AiSymbol symbol = binding.Symbols[c.Operand];
        long count;
        switch (symbol.Kind)
        {
            case AiSymbolKind.Class:
                count = enemy ? census.EnemyCompleted(world, slot, symbol.Id) : census.Player(slot).Completed[symbol.Id];
                break;

            case AiSymbolKind.Units:
                count = enemy ? census.EnemyUnits(world, slot) : census.Player(slot).Units;
                break;

            case AiSymbolKind.Resource:
                // A stock is compared with the number as is ('gold 300'); a '%' or 'enemy' on a resource has no known meaning.
                return !enemy && world.Players[slot].Resources[symbol.Id] >= c.Number;

            case AiSymbolKind.Technology:
                return options.Technologies.StateOf(world, slot, binding.Program.Symbols[c.Operand], symbol.Id) != AiTechnologyState.NotResearched;

            default:
                return false; // unknown names count 0; goal pseudo-objects are never "had"
        }

        if (!c.Percent)
        {
            return count >= c.Number;
        }

        // A share of the population limit (UNVERIFIED): the peasant shares of the deathmatch and start modules and the
        // start module's ten-peasant gate only work out against the limit, not against the current units (ADR 0050).
        long basis = enemy ? EnemyPopulationLimit(world, slot) : world.Players[slot].PopulationLimit;
        return count * 100 >= (long)c.Number * basis;
    }

    // Runs one action of an active rule; returns whether it is "met" (only goals can be unmet).
    private static bool Execute(World world, AiCensus census, AiBinding binding, AiPlayerState state, AiOptions options, List<AiChatMessage> chat, AiActionCode a, int pc, bool wasActive)
    {
        AiProgram program = binding.Program;
        switch (a.Op)
        {
            case AiActionOp.SetFlag:
                AiPlayerState.Set(state.Flags, a.Operand, true);
                return true;

            case AiActionOp.ClearFlag:
                AiPlayerState.Set(state.Flags, a.Operand, false);
                return true;

            case AiActionOp.Forget:
                foreach (int rule in program.RulesByName[a.Operand])
                {
                    AiPlayerState.Set(state.Forgotten, rule, true);
                }

                return true;

            case AiActionOp.ForgetThis:
                AiPlayerState.Set(state.Forgotten, pc, true);
                return true;

            case AiActionOp.Say:
                if (!wasActive)
                {
                    chat.Add(new AiChatMessage(world.Tick, state.Settings.Slot, program.Texts[a.Operand]));
                }

                return true;

            case AiActionOp.Goal:
                return Goal(world, census, binding, state, options, a, pc);

            default:
                return true;
        }
    }

    private static bool Goal(World world, AiCensus census, AiBinding binding, AiPlayerState state, AiOptions options, AiActionCode a, int pc)
    {
        byte slot = state.Settings.Slot;
        AiSymbol symbol = binding.Symbols[a.Operand];
        switch (symbol.Kind)
        {
            case AiSymbolKind.Class:
            {
                if (symbol.OwnType < 0)
                {
                    return true; // nothing the civilization can make: ignored (bind warning)
                }

                AiCensusPlayer player = census.Player(slot);
                int target = ObjectTarget(a.Number, a.Percent, world.Players[slot].PopulationLimit);
                bool met = player.Completed[symbol.Id] >= target;
                if (!met)
                {
                    Push(state, options, new AiGoal(AiGoalKind.Object, a.Operand, a.Number, a.Percent, state.LockedResources, pc));
                }

                return met;
            }

            case AiSymbolKind.Resource:
            {
                int amount = a.Number < 0 ? 1 : a.Number;
                bool met = world.Players[slot].Resources[symbol.Id] >= amount;
                if (!met)
                {
                    state.UnmetResources |= (byte)(1 << Math.Min(symbol.Id, 7));
                    Push(state, options, new AiGoal(AiGoalKind.Resource, a.Operand, a.Number, false, state.LockedResources, pc));
                }

                return met;
            }

            case AiSymbolKind.Technology:
            {
                AiTechnologyState tech = options.Technologies.StateOf(world, slot, binding.Program.Symbols[a.Operand], symbol.Id);
                if (tech == AiTechnologyState.NotResearched)
                {
                    Push(state, options, new AiGoal(AiGoalKind.Technology, a.Operand, a.Number, false, state.LockedResources, pc));
                    return false;
                }

                return true; // researched, or not in the ruleset (treated as researched, ADR 0050)
            }

            case AiSymbolKind.Convert:
                Push(state, options, new AiGoal(AiGoalKind.Convert, a.Operand, a.Number, false, state.LockedResources, pc));
                return true; // no tavern in the sim yet: never blocks a gate (UNVERIFIED)

            case AiSymbolKind.NeedsLocked:
                state.LockedResources |= state.UnmetResources;
                return true;

            case AiSymbolKind.Resign:
                if (state.Settings.MayResign)
                {
                    state.ResignRequested = true;
                }

                return true;

            default:
                return true; // unknown names and 'units' as a goal are ignored
        }
    }

    /// <summary>The count an object goal asks for: N (default 1), or N % of the population limit rounded up (UNVERIFIED).</summary>
    internal static int ObjectTarget(int number, bool percent, int populationLimit)
    {
        int n = number < 0 ? 1 : number;
        return percent ? (int)(((long)n * populationLimit + 99) / 100) : n;
    }

    private static long EnemyPopulationLimit(World world, byte slot)
    {
        long n = 0;
        for (int q = 0; q < world.Players.Length; q++)
        {
            if (AiCensus.IsEnemy(world, slot, q))
            {
                n += world.Players[q].PopulationLimit;
            }
        }

        return n;
    }

    private static void Push(AiPlayerState state, AiOptions options, AiGoal goal)
    {
        if (state.Pending.Count < options.MaxGoalsPerPass)
        {
            state.Pending.Add(goal);
        }
    }
}

/// <summary>
/// Evaluates <c>extern</c> expressions (Appendix C.6) for an AI: <c>$aik</c> (and <c>$kingdom</c>) is the AI's kingdom
/// number 1-8, <c>$gold</c>/<c>$wood</c>/… its stock, <c>@gold(k)</c>… kingdom k's stock, <c>@rnd(a, b)</c> a draw in
/// [a, b] from the AI's stream. Values are 32-bit and saturate; division by zero gives 0; unknown names are 0
/// (UNVERIFIED, UNK-41).
/// </summary>
internal static class AiExternEvaluator
{
    private const int MaxDepth = AiExpressionLimits.MaxDepth + 8; // the compiler rejects deeper trees

    public static int Evaluate(World world, byte slot, AiExpression e, int depth)
    {
        if (depth > MaxDepth)
        {
            return 0;
        }

        switch (e)
        {
            case AiNumberExpression n:
                return n.Value;

            case AiVariableExpression v:
                string name = v.Name;
                if (string.Equals(name, "aik", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "kingdom", StringComparison.OrdinalIgnoreCase))
                {
                    return slot + 1;
                }

                int resource = world.Ruleset.ResourceId(name);
                return resource >= 0 ? world.Players[slot].Resources[resource] : 0;

            case AiCallExpression call:
                string function = call.Name;
                if (string.Equals(function, "rnd", StringComparison.OrdinalIgnoreCase) && call.Arguments.Length == 2)
                {
                    int lo = Evaluate(world, slot, call.Arguments[0], depth + 1);
                    int hi = Evaluate(world, slot, call.Arguments[1], depth + 1);
                    if (hi < lo)
                    {
                        (lo, hi) = (hi, lo);
                    }

                    long span = (long)hi - lo + 1;
                    return span > int.MaxValue ? lo : lo + world.Rng.Ai(slot).NextInt((int)span);
                }

                int r = world.Ruleset.ResourceId(function);
                if (r >= 0 && call.Arguments.Length == 1)
                {
                    int kingdom = Evaluate(world, slot, call.Arguments[0], depth + 1) - 1;
                    return world.HasPlayer(kingdom) ? world.Players[kingdom].Resources[r] : 0;
                }

                return 0;

            case AiUnaryExpression u:
                int operand = Evaluate(world, slot, u.Operand, depth + 1);
                return u.Operator == AiExpressionOperator.Not ? (operand == 0 ? 1 : 0) : Saturate(-(long)operand);

            case AiBinaryExpression b:
                return Binary(world, slot, b, depth);

            default:
                return 0;
        }
    }

    private static int Binary(World world, byte slot, AiBinaryExpression b, int depth)
    {
        int left = Evaluate(world, slot, b.Left, depth + 1);
        switch (b.Operator)
        {
            case AiExpressionOperator.And:
                return left != 0 && Evaluate(world, slot, b.Right, depth + 1) != 0 ? 1 : 0;
            case AiExpressionOperator.Or:
                return left != 0 || Evaluate(world, slot, b.Right, depth + 1) != 0 ? 1 : 0;
        }

        long l = left;
        long r = Evaluate(world, slot, b.Right, depth + 1);
        return b.Operator switch
        {
            AiExpressionOperator.Equal => l == r ? 1 : 0,
            AiExpressionOperator.NotEqual => l != r ? 1 : 0,
            AiExpressionOperator.Less => l < r ? 1 : 0,
            AiExpressionOperator.LessOrEqual => l <= r ? 1 : 0,
            AiExpressionOperator.Greater => l > r ? 1 : 0,
            AiExpressionOperator.GreaterOrEqual => l >= r ? 1 : 0,
            AiExpressionOperator.Add => Saturate(l + r),
            AiExpressionOperator.Subtract => Saturate(l - r),
            AiExpressionOperator.Multiply => Saturate(l * r),
            AiExpressionOperator.Divide => r == 0 ? 0 : Saturate(l / r),
            AiExpressionOperator.Remainder => r == 0 ? 0 : (int)(l % r),
            _ => 0,
        };
    }

    private static int Saturate(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);
}
