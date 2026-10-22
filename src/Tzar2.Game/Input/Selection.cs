using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;

namespace Tzar2.Game.Input;

/// <summary>What the input layer knows of an entity (from the latest frame).</summary>
/// <param name="Id">The entity.</param>
/// <param name="TypeId">Type id.</param>
/// <param name="Owner">Owner slot (0xFF = nature).</param>
/// <param name="Category">Unit, building, resource...</param>
/// <param name="IsMilitary">Armed and not a worker (Alt+double-click splits on this).</param>
/// <param name="State">Current activity, for Ctrl+double-click "same state".</param>
/// <param name="Health">Hit points.</param>
/// <param name="MaxHealth">Maximum hit points.</param>
/// <param name="CanHarvest">Has the Harvester trait.</param>
/// <param name="CanBuild">Has the Builder trait.</param>
/// <param name="IsArmed">Has an Armament.</param>
/// <param name="IsProducer">Trains units (has a rally point).</param>
/// <param name="IsResourceNode">A tree, rock or field.</param>
/// <param name="UnderConstruction">A construction site.</param>
/// <param name="IsMobile">Moves.</param>
/// <param name="IsRepairable">Builders can repair it when damaged (buildings, ships, siege weapons; GAME-03).</param>
/// <param name="CanCapture">Captures enemy buildings (peasants; GAME-04).</param>
/// <param name="IsCapturable">Peasants can capture it now (its capture technology, if any, is researched).</param>
/// <param name="CanPickItems">Carries items (an inventory; GAME-10).</param>
/// <param name="IsItem">An item lying on the ground (GAME-10).</param>
/// <param name="Level">Experience level (GAME-08), or -1 for what gains none.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
public readonly record struct EntityInfo(
    EntityId Id,
    ushort TypeId,
    byte Owner,
    EntityCategory Category,
    bool IsMilitary,
    int State,
    int Health,
    int MaxHealth,
    bool CanHarvest,
    bool CanBuild,
    bool IsArmed,
    bool IsProducer,
    bool IsResourceNode,
    bool UnderConstruction,
    bool IsMobile,
    bool IsRepairable = false,
    bool CanCapture = false,
    bool IsCapturable = false,
    bool CanPickItems = false,
    bool IsItem = false,
    int Level = -1,
    int X = 0,
    int Y = 0);

/// <summary>
/// The local selection and control groups with the original's mouse semantics (Appendix E.6, HK.TXT): click, Shift+click
/// add/remove, drag box, double-click same type on screen, Ctrl+double-click same type and state, Alt+double-click
/// military or non-military, Alt+Ctrl+double-click military/non-military with the same state, Ctrl+digit assign,
/// digit select, Ctrl+0 un-group, Tab focus cycling, health filters and "ungrouped". Engine-free and unit-tested.
/// </summary>
public sealed class SelectionModel
{
    private readonly List<EntityId> _selected = [];
    private readonly List<EntityId>[] _groups = new List<EntityId>[10];

    /// <summary>Creates an empty selection for <paramref name="localPlayer"/>.</summary>
    public SelectionModel(byte localPlayer)
    {
        LocalPlayer = localPlayer;
        for (int i = 0; i < _groups.Length; i++)
        {
            _groups[i] = [];
        }
    }

    /// <summary>The local slot.</summary>
    public byte LocalPlayer { get; }

    /// <summary>Selected entities in selection order.</summary>
    public IReadOnlyList<EntityId> Selected => _selected;

    /// <summary>The entity Tab shows individually (index into <see cref="Selected"/>).</summary>
    public int Focus { get; private set; }

    /// <summary>Raised when the selection changes.</summary>
    public event Action? Changed;

    /// <summary>Whether the selection is a group of own entities (orders can be given).</summary>
    public bool IsOwn { get; private set; }

    /// <summary>Members of control group <paramref name="digit"/> (1-9).</summary>
    public IReadOnlyList<EntityId> Group(int digit) => _groups[digit];

    /// <summary>Whether <paramref name="id"/> is selected.</summary>
    public bool Contains(EntityId id) => _selected.Contains(id);

    /// <summary>Clears the selection.</summary>
    public void Clear() => Set([], own: false);

    /// <summary>A left click on <paramref name="hit"/> (null = empty ground).</summary>
    public void Click(EntityInfo? hit, bool shift)
    {
        if (hit is not { } h)
        {
            if (!shift)
            {
                Clear();
            }

            return;
        }

        bool own = h.Owner == LocalPlayer;
        if (shift && own && IsOwn && h.IsMobile)
        {
            var next = new List<EntityId>(_selected);
            if (!next.Remove(h.Id))
            {
                next.Add(h.Id);
            }

            Set(next, own: true);
            return;
        }

        Set([h.Id], own);
    }

    /// <summary>A drag box over <paramref name="inBox"/>: own units win; otherwise a single own building or nothing.</summary>
    public void Box(IReadOnlyList<EntityInfo> inBox, bool shift)
    {
        ArgumentNullException.ThrowIfNull(inBox);
        var units = new List<EntityId>(shift && IsOwn ? _selected : []);
        foreach (EntityInfo e in inBox)
        {
            if (e.Owner == LocalPlayer && e.IsMobile && !units.Contains(e.Id))
            {
                units.Add(e.Id);
            }
        }

        if (units.Count > 0)
        {
            Set(units, own: true);
            return;
        }

        foreach (EntityInfo e in inBox)
        {
            if (e.Owner == LocalPlayer)
            {
                Set([e.Id], own: true);
                return;
            }
        }

        if (!shift)
        {
            Clear();
        }
    }

    /// <summary>A double click on <paramref name="hit"/> with the entities on screen.</summary>
    public void DoubleClick(EntityInfo hit, IReadOnlyList<EntityInfo> onScreen, bool ctrl, bool alt)
    {
        ArgumentNullException.ThrowIfNull(onScreen);
        if (hit.Owner != LocalPlayer)
        {
            Set([hit.Id], own: false);
            return;
        }

        var list = new List<EntityId>();
        foreach (EntityInfo e in onScreen)
        {
            if (e.Owner != LocalPlayer || e.Category != hit.Category)
            {
                continue;
            }

            bool sameKind = alt ? e.IsMilitary == hit.IsMilitary && e.IsMobile == hit.IsMobile : e.TypeId == hit.TypeId;
            if (sameKind && (!ctrl || e.State == hit.State))
            {
                list.Add(e.Id);
            }
        }

        if (!list.Contains(hit.Id))
        {
            list.Insert(0, hit.Id);
        }

        Set(list, own: true);
    }

    /// <summary>Ctrl+digit: the selection becomes group <paramref name="digit"/> (its members leave other groups).</summary>
    public void AssignGroup(int digit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digit, 9);
        if (!IsOwn)
        {
            return;
        }

        foreach (List<EntityId> group in _groups)
        {
            group.RemoveAll(_selected.Contains);
        }

        _groups[digit].Clear();
        _groups[digit].AddRange(_selected);
    }

    /// <summary>Ctrl+0: the selected entities leave their groups.</summary>
    public void ClearGroup()
    {
        foreach (List<EntityId> group in _groups)
        {
            group.RemoveAll(_selected.Contains);
        }
    }

    /// <summary>Digit: selects group <paramref name="digit"/>; returns true when it already was the selection (centre on it).</summary>
    public bool SelectGroup(int digit, Func<EntityId, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        List<EntityId> group = _groups[digit];
        group.RemoveAll(id => !alive(id));
        if (group.Count == 0)
        {
            return false;
        }

        bool same = IsOwn && _selected.Count == group.Count && group.TrueForAll(_selected.Contains);
        Set(new List<EntityId>(group), own: true);
        return same;
    }

    /// <summary>Alt+\: own units on screen that are in no group.</summary>
    public void SelectUngrouped(IReadOnlyList<EntityInfo> onScreen)
    {
        ArgumentNullException.ThrowIfNull(onScreen);
        var list = new List<EntityId>();
        foreach (EntityInfo e in onScreen)
        {
            if (e.Owner == LocalPlayer && e.IsMobile && GroupOf(e.Id) == 0)
            {
                list.Add(e.Id);
            }
        }

        if (list.Count > 0)
        {
            Set(list, own: true);
        }
    }

    /// <summary>
    /// Ctrl+] / Ctrl+[: of the selection, those with more or less experience than its average level; with nothing
    /// selected, the own units on screen above level 7 or below level 8 (TZAR110.TXT, 1.09; EXP-007).
    /// </summary>
    public void SelectByExperience(IReadOnlyList<EntityInfo> candidates, bool above)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        bool fromSelection = IsOwn && _selected.Count > 0;
        var pool = new List<EntityInfo>();
        long sum = 0;
        foreach (EntityInfo e in candidates)
        {
            if (e.Owner == LocalPlayer && e.IsMobile && e.Level >= 0 && (!fromSelection || _selected.Contains(e.Id)))
            {
                pool.Add(e);
                sum += e.Level;
            }
        }

        var list = new List<EntityId>();
        foreach (EntityInfo e in pool)
        {
            long scaled = (long)e.Level * pool.Count; // compared with the sum: no rounding of the average
            bool pick = fromSelection ? (above ? scaled > sum : scaled < sum) : (above ? e.Level > 7 : e.Level < 8);
            if (pick)
            {
                list.Add(e.Id);
            }
        }

        if (list.Count > 0)
        {
            Set(list, own: true);
        }
    }

    /// <summary>
    /// Alt+] / Alt+[: of the selection (or, with nothing selected, the own units on screen against 50 %), those above or
    /// below the average health share.
    /// </summary>
    public void SelectByHealth(IReadOnlyList<EntityInfo> candidates, bool above)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        bool fromSelection = IsOwn && _selected.Count > 0;
        var pool = new List<EntityInfo>();
        foreach (EntityInfo e in candidates)
        {
            if (e.Owner == LocalPlayer && e.IsMobile && e.MaxHealth > 0 && (!fromSelection || _selected.Contains(e.Id)))
            {
                pool.Add(e);
            }
        }

        if (pool.Count == 0)
        {
            return;
        }

        // Shares in per-mille; the average of the pool, or 500 without a selection.
        long sum = 0;
        foreach (EntityInfo e in pool)
        {
            sum += e.Health * 1000L / e.MaxHealth;
        }

        long average = fromSelection ? sum / pool.Count : 500;
        var list = new List<EntityId>();
        foreach (EntityInfo e in pool)
        {
            long share = e.Health * 1000L / e.MaxHealth;
            if (above ? share > average : share < average)
            {
                list.Add(e.Id);
            }
        }

        if (list.Count > 0)
        {
            Set(list, own: true);
        }
    }

    /// <summary>Selects the given own entities (Ctrl+letter building classes, events).</summary>
    public void SelectOwn(IReadOnlyList<EntityId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Set(new List<EntityId>(ids), own: true);
    }

    /// <summary>Tab: the next selected entity becomes the focus (shown individually).</summary>
    public void CycleFocus()
    {
        if (_selected.Count > 0)
        {
            Focus = (Focus + 1) % _selected.Count;
            Changed?.Invoke();
        }
    }

    /// <summary>Drops entities that no longer exist (call once per frame).</summary>
    public void Prune(Func<EntityId, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        if (_selected.RemoveAll(id => !alive(id)) > 0)
        {
            Focus = Math.Min(Focus, Math.Max(0, _selected.Count - 1));
            if (_selected.Count == 0)
            {
                IsOwn = false;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>The group digit of <paramref name="id"/>, or 0.</summary>
    public int GroupOf(EntityId id)
    {
        for (int d = 1; d < _groups.Length; d++)
        {
            if (_groups[d].Contains(id))
            {
                return d;
            }
        }

        return 0;
    }

    private void Set(List<EntityId> ids, bool own)
    {
        _selected.Clear();
        _selected.AddRange(ids);
        IsOwn = own && ids.Count > 0;
        Focus = 0;
        Changed?.Invoke();
    }
}
