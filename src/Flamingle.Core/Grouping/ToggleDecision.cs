namespace Flamingle.Core.Grouping;

public enum FlockAction
{
    /// <summary>Minimise every member.</summary>
    Minimise,

    /// <summary>Restore any minimised members and bring the whole flock to the front.</summary>
    Restore,
}

/// <summary>
/// What one press of a flock's toggle should do. The rule is "bring it forward, then put it away": if the
/// flock is already fully up and in front (the focused window is a member), minimise it; otherwise
/// restore / raise it. That way a press from inside some other app always brings the flock to you first,
/// rather than minimising windows that were merely hidden behind something.
/// </summary>
public static class ToggleDecision
{
    /// <param name="memberMinimised">One flag per live member — true if that member is minimised.</param>
    /// <param name="focusInFlock">True if the focused window is a member of this flock.</param>
    public static FlockAction Decide(IReadOnlyCollection<bool> memberMinimised, bool focusInFlock)
    {
        // Any member down (all, or just some) → bring the whole flock back.
        if (memberMinimised.Count == 0 || memberMinimised.Any(m => m)) return FlockAction.Restore;
        return focusInFlock ? FlockAction.Minimise : FlockAction.Restore;
    }
}
