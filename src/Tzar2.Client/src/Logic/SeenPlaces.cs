using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// Where the entities of the frames stood, by entity index, for the sounds and pictures the sim's events call for there
/// (PRES-012, PRES-013). An entity counts while the frames show it and for one frame after: a frame comes before the
/// events of its ticks, so a unit killed since the frame before is only in that one. The frames show what the player sees,
/// so a unit gone into the fog, one that died there, or an index a unit never seen has taken again, is not heard where it
/// was last seen (the original hears effects only where the fog is lifted, 0x505610; the seventh play-test: sounds hung on
/// where fights had been). Engine-free.
/// </summary>
public sealed class SeenPlaces
{
    private readonly Dictionary<int, Place> _byIndex = [];
    private readonly List<int> _stale = [];
    private int _frame;

    /// <summary>Where an entity stood (world units), its type and owner, and the frame that showed it.</summary>
    public readonly record struct Place(int X, int Y, ushort Type, byte Owner, int Frame);

    /// <summary>A new frame begins; now and then the entities no frame has shown for a while are forgotten.</summary>
    public void BeginFrame()
    {
        _frame++;
        if (_frame % 256 != 0)
        {
            return;
        }

        _stale.Clear();
        foreach ((int index, Place place) in _byIndex)
        {
            if (place.Frame < _frame - 1)
            {
                _stale.Add(index);
            }
        }

        foreach (int index in _stale)
        {
            _byIndex.Remove(index);
        }
    }

    /// <summary>The frame shows the entity with index <paramref name="index"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Saw(int index, int x, int y, ushort type, byte owner) => _byIndex[index] = new Place(x, y, type, owner, _frame);

    /// <summary>Where the entity with index <paramref name="index"/> stood in this frame or the one before; false otherwise.</summary>
    public bool TryGet(int index, out Place place) => _byIndex.TryGetValue(index, out place) && place.Frame >= _frame - 1;
}
