namespace Gaggle.Core.Grouping;

public enum GaggleAction
{
    /// <summary>Minimise every member.</summary>
    Minimise,

    /// <summary>Restore any minimised members and bring the whole gaggle to the front.</summary>
    Restore,
}

/// <summary>
/// What one press of a gaggle's toggle should do. The rule is "bring it forward, then put it away": if the
/// gaggle is already fully up and in front (the focused window is a member), minimise it; otherwise
/// restore / raise it. That way a press from inside some other app always brings the gaggle to you first,
/// rather than minimising windows that were merely hidden behind something.
/// </summary>
public static class ToggleDecision
{
    /// <param name="memberMinimised">One flag per live member — true if that member is minimised.</param>
    /// <param name="focusInGaggle">True if the focused window is a member of this gaggle.</param>
    public static GaggleAction Decide(IReadOnlyCollection<bool> memberMinimised, bool focusInGaggle)
    {
        // Any member down (all, or just some) → bring the whole gaggle back.
        if (memberMinimised.Count == 0 || memberMinimised.Any(m => m)) return GaggleAction.Restore;
        return focusInGaggle ? GaggleAction.Minimise : GaggleAction.Restore;
    }
}
