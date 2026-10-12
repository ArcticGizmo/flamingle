namespace Gaggle.Core.Grouping;

/// <summary>
/// Breaks feedback loops. When Gaggle itself minimises / restores / moves a member, the WinEvent hooks
/// report that change back a moment later — indistinguishable from the user doing it. Before acting on a
/// window, call <see cref="Suppress"/>; the hook handlers then ignore events for that window until the
/// window of time expires.
/// </summary>
public sealed class EchoSuppressor
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(1000);

    private readonly Dictionary<long, DateTimeOffset> _until = [];
    private readonly TimeProvider _time;

    public EchoSuppressor(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Suppress(long handle, TimeSpan? window = null)
        => _until[handle] = _time.GetUtcNow() + (window ?? DefaultWindow);

    public bool IsSuppressed(long handle)
    {
        if (!_until.TryGetValue(handle, out var until)) return false;
        if (_time.GetUtcNow() < until) return true;

        _until.Remove(handle);
        return false;
    }
}
