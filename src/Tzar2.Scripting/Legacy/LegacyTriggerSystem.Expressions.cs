using System;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;

namespace Tzar2.Scripting.Legacy;

public sealed partial class LegacyTriggerSystem
{
    // The current object of Object Expression and Set Object Properties (UNK-38), or None.
    private EntityId _current;

    // Steps (expression nodes, object tests) spent in this tick.
    private int _steps;

    /// <summary>Evaluates an expression; an empty or unparsable one gives <paramref name="empty"/>.</summary>
    private int Evaluate(ExpressionDef expression, int empty, EntityId current)
    {
        if (expression.Root is not { } root)
        {
            return empty;
        }

        EntityId previous = _current;
        _current = current;
        try
        {
            return Eval(root);
        }
        finally
        {
            _current = previous;
        }
    }

    private int Eval(Expr node)
    {
        _steps++;
        switch (node)
        {
            case IntLiteral literal:
                return literal.Value;
            case UserVar v:
                return _state.Variables[v.VariableIndex];
            case ReservedVar r:
                return ReadReserved(r.Variable);
            case EventRef e:
                return _state.EventValues[e.EventIndex] ? 1 : 0;
            case TimerRef t:
                return _state.Timers[t.ActionIndex].Started ? ScenarioArithmetic.Saturate(_state.Timers[t.ActionIndex].ValueMs) : 0;
            case UnknownRef:
                return 0;
            case UnaryExpr u:
            {
                int value = Eval(u.Operand);
                return u.Op == UnaryOp.Not ? (value == 0 ? 1 : 0) : ScenarioArithmetic.Negate(value);
            }

            case BinaryExpr b when b.Op == BinaryOp.And:
                return Eval(b.Left) != 0 && Eval(b.Right) != 0 ? 1 : 0;
            case BinaryExpr b when b.Op == BinaryOp.Or:
                return Eval(b.Left) != 0 || Eval(b.Right) != 0 ? 1 : 0;
            case BinaryExpr b:
                return Binary(b.Op, Eval(b.Left), Eval(b.Right));
            case AssignExpr a:
            {
                int value = Eval(a.Value);
                if (a.Target is UserVar target)
                {
                    _state.Variables[target.VariableIndex] = value;
                }
                else if (a.Target is ReservedVar reserved)
                {
                    WriteReserved(reserved.Variable, value);
                }

                return value;
            }

            case CallExpr c:
                return Call(c);
            case SequenceExpr sequence:
            {
                int last = 0;
                foreach (Expr statement in sequence.Statements)
                {
                    last = Eval(statement);
                }

                return last;
            }

            case TextArg:
                return 0;
            default:
                return 0;
        }
    }

    // The i-th argument's value, 0 when missing (the original does not check arity; ADR 0120).
    private int Arg(CallExpr c, int i) => i < c.Arguments.Length ? Eval(c.Arguments[i]) : 0;

    // Built-in functions (Appendix C.6 and the game's further names, ADR 0120). Kingdoms are legacy 1-8. Beyond C.6 the
    // semantics are UNVERIFIED (UNK-64); functions that only drive the original's interface (selection, movies) are
    // no-ops here, and those whose effect the rebuild does not have yet return 0 with a fault.
    private int Call(CallExpr c)
    {
        switch (c.Function)
        {
            case CallKind.Gold:
                return Resource(Arg(c, 0) - 1, _gold);
            case CallKind.Stone:
                return Resource(Arg(c, 0) - 1, _stone);
            case CallKind.Food:
                return Resource(Arg(c, 0) - 1, _food);
            case CallKind.Wood:
                return Resource(Arg(c, 0) - 1, _wood);
            case CallKind.Rnd:
            {
                int lo = Arg(c, 0);
                int hi = Arg(c, 1);
                if (lo > hi)
                {
                    (lo, hi) = (hi, lo);
                }

                // Inclusive bounds (UNVERIFIED, UNK-53); the scripts stream only (REQ-SIM-06).
                long span = (long)hi - lo + 1;
                return (int)(lo + (long)(_world.Rng.Scripts.NextUInt64() % (ulong)span));
            }

            case CallKind.Max:
            case CallKind.Min:
            {
                int value = Arg(c, 0);
                for (int i = 1; i < c.Arguments.Length; i++)
                {
                    int next = Eval(c.Arguments[i]);
                    value = c.Function == CallKind.Max ? Math.Max(value, next) : Math.Min(value, next);
                }

                return value;
            }

            case CallKind.SetGold:
                return SetResource(Arg(c, 0) - 1, _gold, Arg(c, 1));
            case CallKind.SetStone:
                return SetResource(Arg(c, 0) - 1, _stone, Arg(c, 1));
            case CallKind.SetWood:
                return SetResource(Arg(c, 0) - 1, _wood, Arg(c, 1));
            case CallKind.SetFood:
                return SetResource(Arg(c, 0) - 1, _food, Arg(c, 1));
            case CallKind.SetPopLimit:
            {
                int player = Arg(c, 0) - 1;
                int limit = Math.Clamp(Arg(c, 1), 0, 100_000);
                if (_world.HasPlayer(player))
                {
                    _world.Players[player].PopulationLimit = limit;
                }

                return limit;
            }

            case CallKind.HasSharedRes:
                return Pair(c, Sim.Kingdoms.Sharing.Resources, null);
            case CallKind.HasSharedUnits:
                return Pair(c, Sim.Kingdoms.Sharing.UnitControl, null);
            case CallKind.HasSharedVision:
                return Pair(c, Sim.Kingdoms.Sharing.Vision, null);
            case CallKind.IsAlly:
                return Pair(c, Sim.Kingdoms.Sharing.None, Sim.Kingdoms.Stance.Ally);
            case CallKind.IsEnemy:
                return Pair(c, Sim.Kingdoms.Sharing.None, Sim.Kingdoms.Stance.Enemy);
            case CallKind.IsNeutral:
                return Pair(c, Sim.Kingdoms.Sharing.None, Sim.Kingdoms.Stance.Neutral);
            case CallKind.GetDiplomacy:
            {
                // The IR's stance order (Ally 0, Enemy 1, Neutral 2; UNVERIFIED encoding).
                int a = Arg(c, 0) - 1;
                int b = Arg(c, 1) - 1;
                if (!_world.HasPlayer(a) || !_world.HasPlayer(b) || a == b)
                {
                    return 0;
                }

                return _world.Diplomacy.StanceOf(a, b) switch { Sim.Kingdoms.Stance.Ally => 0, Sim.Kingdoms.Stance.Enemy => 1, _ => 2 };
            }

            case CallKind.GetTime:
                return _world.MinuteAt(_world.Tick.Value) / 60; // the hour of the day clock (UNVERIFIED unit, UNK-64)
            case CallKind.Select:
            case CallKind.Unselect:
            case CallKind.ClearSelection:
            case CallKind.PlayMovie:
            case CallKind.Str:
                EvalArguments(c);
                return 0;
            default:
                // SetTime, God, SetAi, KingdomCanRescue, SetNoAttackTime, Count, InProgress, Build: not yet (UNK-64).
                EvalArguments(c);
                Fault(ScenarioFault.UnsupportedFunction);
                return 0;
        }
    }

    private void EvalArguments(CallExpr c)
    {
        for (int i = 0; i < c.Arguments.Length; i++)
        {
            Eval(c.Arguments[i]);
        }
    }

    // A test between two legacy kingdoms: a sharing (both offer it, allies) or a stance of the first towards the second.
    private int Pair(CallExpr c, Sim.Kingdoms.Sharing sharing, Sim.Kingdoms.Stance? stance)
    {
        int a = Arg(c, 0) - 1;
        int b = Arg(c, 1) - 1;
        if (!_world.HasPlayer(a) || !_world.HasPlayer(b) || a == b)
        {
            return 0;
        }

        bool pass = stance is { } st ? _world.Diplomacy.StanceOf(a, b) == st : _world.Diplomacy.Shares(a, b, sharing);
        return pass ? 1 : 0;
    }

    private int SetResource(int player, int resource, int amount)
    {
        amount = Math.Max(0, amount);
        if (_world.HasPlayer(player) && resource >= 0)
        {
            _world.Players[player].Resources[resource] = amount;
        }

        return amount;
    }

    private int Binary(BinaryOp op, int a, int b)
    {
        switch (op)
        {
            case BinaryOp.Add:
                return ScenarioArithmetic.Add(a, b);
            case BinaryOp.Subtract:
                return ScenarioArithmetic.Subtract(a, b);
            case BinaryOp.Multiply:
                return ScenarioArithmetic.Multiply(a, b);
            case BinaryOp.Divide:
            case BinaryOp.Modulo:
                if (b == 0)
                {
                    Fault(ScenarioFault.DivisionByZero);
                }

                return op == BinaryOp.Divide ? ScenarioArithmetic.Divide(a, b) : ScenarioArithmetic.Remainder(a, b);
            case BinaryOp.Less:
                return a < b ? 1 : 0;
            case BinaryOp.LessOrEqual:
                return a <= b ? 1 : 0;
            case BinaryOp.Greater:
                return a > b ? 1 : 0;
            case BinaryOp.GreaterOrEqual:
                return a >= b ? 1 : 0;
            case BinaryOp.Equal:
                return a == b ? 1 : 0;
            case BinaryOp.NotEqual:
                return a != b ? 1 : 0;
            default:
                return 0;
        }
    }

    // A kingdom's stock of a resource (kingdom 0-based), 0 for an unknown kingdom or resource.
    private int Resource(int player, int resource)
        => _world.HasPlayer(player) && resource >= 0 ? _world.Players[player].Resources[resource] : 0;

    private int ReadReserved(ReservedVariable variable)
    {
        EntityId e = _current;
        if (e == EntityId.None || !_world.Entities.IsAlive(e) || !_world.Identities.Has(e))
        {
            return 0; // no current object (a rule condition): UNVERIFIED, UNK-38
        }

        Identity identity = _world.Identities.Get(e);
        switch (variable)
        {
            case ReservedVariable.Health:
                return _world.Healths.Has(e) ? _world.Healths.Get(e).Current : 0;
            case ReservedVariable.MaxHealth:
                return _world.Healths.Has(e) ? _world.Healths.Get(e).Max : 0;
            case ReservedVariable.Attack:
                return GameplayQueries.AttackOf(_world, e);
            case ReservedVariable.Defense:
                return GameplayQueries.DefenceOf(_world, e);
            case ReservedVariable.Level:
                return _world.Experiences.Has(e) ? _world.Experiences.Get(e).Level : 0;
            case ReservedVariable.Manna:
                return _world.Manas.Has(e) ? _world.Manas.Get(e).MilliMana / 1000 : 0;
            case ReservedVariable.MaxManna:
                return _world.TypeOf(e).Traits.ManaPool?.Max ?? 0;
            case ReservedVariable.Kingdom:
                return _world.HasPlayer(identity.Owner) ? identity.Owner + 1 : 0;
            case ReservedVariable.X:
                return _world.Positions.Get(e).X / Sim.Map.MapState.UnitsPerTile;
            case ReservedVariable.Y:
                return _world.Positions.Get(e).Y / Sim.Map.MapState.UnitsPerTile;
            case ReservedVariable.Gold or ReservedVariable.Stone or ReservedVariable.Food or ReservedVariable.Wood:
                return Resource(identity.Owner, ResourceOf(variable));
            case ReservedVariable.InHolder:
                return _world.IsHeld(e) ? 1 : 0;
            case ReservedVariable.Cargo:
                return _world.Workers.Has(e) ? _world.Workers.Get(e).CarryAmount : 0;
            case ReservedVariable.Race:
                return _world.HasPlayer(identity.Owner) ? _world.Players[identity.Owner].Civilization + 1 : 0;
            case ReservedVariable.UnderFog:
                return UnderFog(e) ? 1 : 0;
            default:
                return 0; // Difficulty, Flags, Program, IsSelected, CanUseDiplomacy, CanDelObjects: UNVERIFIED, UNK-64
        }
    }

    // Hidden from every responder (human) kingdom (UNVERIFIED reading of $underfog, UNK-64).
    private bool UnderFog(EntityId e)
    {
        for (int p = 0; p < _world.Players.Length; p++)
        {
            if ((_options.ResponderMask & (1u << p)) != 0 && _world.Visibility.CanSee(_world, p, e))
            {
                return false;
            }
        }

        return true;
    }

    private int ResourceOf(ReservedVariable variable) => variable switch
    {
        ReservedVariable.Gold => _gold,
        ReservedVariable.Stone => _stone,
        ReservedVariable.Food => _food,
        _ => _wood,
    };

    // Set Object Properties / assignments in Object Expression (UNVERIFIED write semantics, UNK-38).
    private void WriteReserved(ReservedVariable variable, int value)
    {
        EntityId e = _current;
        if (e == EntityId.None || !_world.Entities.IsAlive(e) || !_world.Identities.Has(e))
        {
            Fault(ScenarioFault.ReadOnlyVariable);
            return;
        }

        Identity identity = _world.Identities.Get(e);
        switch (variable)
        {
            case ReservedVariable.Health when _world.Healths.Has(e):
            {
                ref Health h = ref _world.Healths.Get(e);
                if (value <= 0)
                {
                    _world.Kill(e);
                    return;
                }

                h.Current = Math.Min(value, h.Max);
                return;
            }

            case ReservedVariable.MaxHealth when _world.Healths.Has(e):
            {
                ref Health h = ref _world.Healths.Get(e);
                h.Max = Math.Clamp(value, 1, 1_000_000);
                h.Current = Math.Min(h.Current, h.Max);
                return;
            }

            case ReservedVariable.Attack when _world.Experiences.Has(e):
            {
                ref Experience x = ref _world.Experiences.Get(e);
                x.BonusAttack = Math.Clamp(x.BonusAttack + (value - GameplayQueries.AttackOf(_world, e)), 0, 100_000);
                return;
            }

            case ReservedVariable.Defense when _world.Experiences.Has(e):
            {
                ref Experience x = ref _world.Experiences.Get(e);
                x.BonusDefence = Math.Clamp(x.BonusDefence + (value - GameplayQueries.DefenceOf(_world, e)), 0, 100_000);
                return;
            }

            case ReservedVariable.Level when _world.Experiences.Has(e):
                _world.Experiences.Get(e).Level = (ushort)Math.Clamp(value, 0, 10_000);
                _world.RefreshMaxHealth(e);
                return;
            case ReservedVariable.Manna when _world.Manas.Has(e):
            {
                int max = _world.TypeOf(e).Traits.ManaPool?.Max ?? 0;
                _world.Manas.Get(e).MilliMana = (int)Math.Clamp((long)value * 1000, 0, (long)max * 1000);
                return;
            }

            case ReservedVariable.Kingdom when _world.HasPlayer(value - 1) && value - 1 != identity.Owner:
                _world.ChangeOwner(e, (byte)(value - 1));
                return;
            case ReservedVariable.Kingdom:
                return;
            case ReservedVariable.Gold or ReservedVariable.Stone or ReservedVariable.Food or ReservedVariable.Wood when _world.HasPlayer(identity.Owner) && ResourceOf(variable) >= 0:
                _world.Players[identity.Owner].Resources[ResourceOf(variable)] = Math.Max(0, value);
                return;
            default:
                Fault(ScenarioFault.ReadOnlyVariable);
                return;
        }
    }

    private void Fault(ScenarioFault fault)
        => _world.Events.Emit(new SimEvent(SimEventKind.ScenarioFault, 0xFF, EntityId.None, _rule, _action, (int)fault));
}
