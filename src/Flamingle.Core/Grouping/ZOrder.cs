namespace Flamingle.Core.Grouping;

public static class ZOrder
{
    /// <summary>
    /// Sort <paramref name="handles"/> topmost-first according to <paramref name="zOrderTopFirst"/> (the
    /// order <c>EnumWindows</c> reports). Handles not found there keep their relative order, at the bottom.
    /// </summary>
    public static IReadOnlyList<long> Sort(IEnumerable<long> handles, IReadOnlyList<long> zOrderTopFirst)
    {
        var rank = new Dictionary<long, int>(zOrderTopFirst.Count);
        for (var i = 0; i < zOrderTopFirst.Count; i++)
            rank.TryAdd(zOrderTopFirst[i], i);

        return handles
            .Select((h, i) => (h, i))
            .OrderBy(x => rank.TryGetValue(x.h, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.h)
            .ToList();
    }

    /// <summary>Move <paramref name="top"/> to the front, keeping everything else in order.</summary>
    public static IReadOnlyList<long> BringToFront(IReadOnlyList<long> order, long top)
        => order.Contains(top) ? [top, .. order.Where(h => h != top)] : order;
}
