using Flamingle.Core.Geometry;

namespace Flamingle.Core.Zones;

/// <summary>Where a layout is and how it's split — everything a position adjustment can change.</summary>
public sealed record LayoutGeometry(string DeviceName, string LayoutId, PixelRect Frame, IReadOnlyList<ZoneFraction>? CustomZones)
{
    public static LayoutGeometry Of(ZoneContainer c) => new(c.DeviceName, c.LayoutId, c.Frame, c.CustomZones?.ToList());

    /// <summary>Put a container back to this geometry (the preset first — setting it clears custom splits).</summary>
    public void ApplyTo(ZoneContainer c)
    {
        c.LayoutId = LayoutId;
        c.CustomZones = CustomZones?.ToList();
        c.Frame = Frame;
        c.DeviceName = DeviceName;
    }

    public bool SameAs(LayoutGeometry other)
        => DeviceName == other.DeviceName && LayoutId == other.LayoutId && Frame == other.Frame
           && (CustomZones ?? []).SequenceEqual(other.CustomZones ?? []);
}

/// <summary>One undoable step: what it was called, and the geometry of every layout it touched beforehand.</summary>
public sealed record LayoutChange<TKey>(string Label, IReadOnlyDictionary<TKey, LayoutGeometry> Before) where TKey : notnull;

/// <summary>
/// Undo / redo for layout position adjustments (moves, splits, layout switches). Each step stores the geometry of
/// the layouts it touched <em>before</em> the change; undoing swaps that with their geometry now, which becomes the
/// redo step. A new change clears the redo steps. Session-only (not saved).
/// </summary>
public sealed class LayoutHistory<TKey> where TKey : notnull
{
    private readonly int _limit;
    private readonly LinkedList<LayoutChange<TKey>> _undo = new();
    private readonly Stack<LayoutChange<TKey>> _redo = new();

    public LayoutHistory(int limit = 50) => _limit = limit;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Record a change, given the geometry before it and after it. Layouts it didn't actually change are
    /// left out; if it changed nothing, nothing is recorded. Returns whether a step was recorded.</summary>
    public bool Record(string label, IReadOnlyDictionary<TKey, LayoutGeometry> before, IReadOnlyDictionary<TKey, LayoutGeometry> after)
    {
        var changed = before
            .Where(kv => !after.TryGetValue(kv.Key, out var now) || !now.SameAs(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (changed.Count == 0) return false;

        _undo.AddLast(new LayoutChange<TKey>(label, changed));
        if (_undo.Count > _limit) _undo.RemoveFirst();
        _redo.Clear();
        return true;
    }

    /// <summary>The step to undo, swapped for its redo (built from <paramref name="current"/>). Null if none.</summary>
    public LayoutChange<TKey>? Undo(Func<TKey, LayoutGeometry?> current)
    {
        if (_undo.Last is not { } node) return null;
        _undo.RemoveLast();
        _redo.Push(Inverse(node.Value, current));
        return node.Value;
    }

    /// <summary>The step to redo, swapped back onto the undo list. Null if none.</summary>
    public LayoutChange<TKey>? Redo(Func<TKey, LayoutGeometry?> current)
    {
        if (!_redo.TryPop(out var step)) return null;
        _undo.AddLast(Inverse(step, current));
        return step;
    }

    private static LayoutChange<TKey> Inverse(LayoutChange<TKey> step, Func<TKey, LayoutGeometry?> current)
        => new(step.Label, step.Before.Keys
            .Select(k => (k, current(k)))
            .Where(x => x.Item2 is not null)
            .ToDictionary(x => x.k, x => x.Item2!));
}
