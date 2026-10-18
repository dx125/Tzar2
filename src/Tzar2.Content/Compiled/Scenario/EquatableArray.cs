using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// An immutable array with element-wise equality, so the scenario records (ADR 0120) compare by value. The hash code
/// is only the length: it must not depend on per-process string hashing (T2D002), and these arrays are never hot
/// dictionary keys.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _items;

    /// <summary>Wraps <paramref name="items"/>.</summary>
    public EquatableArray(ImmutableArray<T> items) => _items = items.IsDefault ? ImmutableArray<T>.Empty : items;

    /// <summary>The empty array.</summary>
    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    /// <summary>The elements.</summary>
    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    /// <inheritdoc/>
    public int Count => Items.Length;

    /// <summary>Number of elements.</summary>
    public int Length => Items.Length;

    /// <inheritdoc/>
    public T this[int index] => Items[index];

    /// <summary>Copies a sequence.</summary>
    public static implicit operator EquatableArray<T>(T[] items) => new(ImmutableArray.Create(items));

    /// <summary>Wraps an immutable array.</summary>
    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);

    /// <summary>Element-wise equality.</summary>
    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    /// <summary>Element-wise inequality.</summary>
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    /// <summary>Copies a sequence.</summary>
    public static EquatableArray<T> From(IEnumerable<T> items) => new(ImmutableArray.CreateRange(items));

    /// <inheritdoc/>
    public bool Equals(EquatableArray<T> other)
    {
        ImmutableArray<T> a = Items;
        ImmutableArray<T> b = other.Items;
        if (a.Length != b.Length)
        {
            return false;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < a.Length; i++)
        {
            if (!comparer.Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Length;

    /// <summary>Enumerates the elements.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();
}
