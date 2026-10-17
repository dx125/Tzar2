using System;
using System.Collections.Generic;

namespace Tzar2.Sim.Ecs;

/// <summary>
/// The component stores of a world, in registration order. The order fixes the hash and snapshot layout, so stores
/// are registered once, before the first tick, and the registry is then sealed.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly List<IComponentStore> _stores = [];

    // Lookup only, never iterated (T2D004).
    private readonly Dictionary<Type, IComponentStore> _byType = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Whether new stores can still be registered.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>All stores in registration order.</summary>
    public IReadOnlyList<IComponentStore> All => _stores;

    /// <summary>Registers a hot store.</summary>
    /// <exception cref="InvalidOperationException">The registry is sealed, or the type or key is already registered.</exception>
    public ComponentStore<T> RegisterHot<T>(string key)
        where T : unmanaged, IComponent
        => Register(typeof(T), new ComponentStore<T>(key));

    /// <summary>Registers a cold store.</summary>
    /// <exception cref="InvalidOperationException">The registry is sealed, or the type or key is already registered.</exception>
    public ColdComponentStore<T> RegisterCold<T>(string key)
        where T : IColdComponent<T>
        => Register(typeof(T), new ColdComponentStore<T>(key));

    /// <summary>The hot store of <typeparamref name="T"/>.</summary>
    /// <exception cref="KeyNotFoundException">Not registered.</exception>
    public ComponentStore<T> Hot<T>()
        where T : unmanaged, IComponent
        => (ComponentStore<T>)Lookup(typeof(T));

    /// <summary>The cold store of <typeparamref name="T"/>.</summary>
    /// <exception cref="KeyNotFoundException">Not registered.</exception>
    public ColdComponentStore<T> Cold<T>()
        where T : IColdComponent<T>
        => (ColdComponentStore<T>)Lookup(typeof(T));

    /// <summary>The store with the snapshot key <paramref name="key"/>, or <c>null</c>.</summary>
    public IComponentStore? FindByKey(string key)
    {
        foreach (IComponentStore store in _stores)
        {
            if (string.Equals(store.Key, key, StringComparison.Ordinal))
            {
                return store;
            }
        }

        return null;
    }

    /// <summary>Prevents further registration.</summary>
    public void Seal() => IsSealed = true;

    private TStore Register<TStore>(Type type, TStore store)
        where TStore : IComponentStore
    {
        if (IsSealed)
        {
            throw new InvalidOperationException("Component stores must be registered before the first tick.");
        }

        if (string.IsNullOrEmpty(store.Key) || !_keys.Add(store.Key) || !_byType.TryAdd(type, store))
        {
            throw new InvalidOperationException($"Component {type.Name} / key '{store.Key}' is already registered or invalid.");
        }

        _stores.Add(store);
        return store;
    }

    private IComponentStore Lookup(Type type)
        => _byType.TryGetValue(type, out IComponentStore? store)
            ? store
            : throw new KeyNotFoundException($"Component {type.Name} is not registered.");
}
