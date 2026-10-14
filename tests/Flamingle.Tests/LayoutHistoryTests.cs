using Flamingle.Core.Geometry;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

public class LayoutHistoryTests
{
    private static LayoutGeometry At(int x, string device = @"\\.\DISPLAY1") => new(device, "columns-2", new PixelRect(x, 0, 1000, 800), null);

    // The "live" geometry the history reads back when undoing / redoing.
    private readonly Dictionary<string, LayoutGeometry> _live = new() { ["a"] = At(0), ["b"] = At(0, @"\\.\DISPLAY2") };

    private Dictionary<string, LayoutGeometry> Snapshot() => new(_live);

    private void Apply(LayoutChange<string>? step)
    {
        foreach (var (k, g) in step!.Before) _live[k] = g;
    }

    [Fact]
    public void Undo_puts_a_layout_back_and_redo_reapplies_it()
    {
        var history = new LayoutHistory<string>();
        var before = Snapshot();
        _live["a"] = At(300);
        Assert.True(history.Record("move", before, Snapshot()));

        Apply(history.Undo(k => _live[k]));
        Assert.Equal(At(0), _live["a"]);

        Apply(history.Redo(k => _live[k]));
        Assert.Equal(At(300), _live["a"]);
    }

    [Fact]
    public void Only_layouts_that_changed_are_recorded()
    {
        var history = new LayoutHistory<string>();
        var before = Snapshot();
        _live["a"] = At(300);
        history.Record("move", before, Snapshot());

        var step = history.Undo(k => _live[k]);
        Assert.Equal(["a"], step!.Before.Keys);
    }

    [Fact]
    public void A_change_that_changed_nothing_is_not_recorded()
    {
        var history = new LayoutHistory<string>();
        Assert.False(history.Record("move", Snapshot(), Snapshot()));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_swap_undoes_both_layouts_at_once()
    {
        var history = new LayoutHistory<string>();
        var before = Snapshot();
        (_live["a"], _live["b"]) = (_live["b"] with { Frame = At(0).Frame }, _live["a"]);
        history.Record("swap", before, Snapshot());

        Apply(history.Undo(k => _live[k]));
        Assert.Equal(@"\\.\DISPLAY1", _live["a"].DeviceName);
        Assert.Equal(@"\\.\DISPLAY2", _live["b"].DeviceName);
    }

    [Fact]
    public void A_new_change_clears_redo()
    {
        var history = new LayoutHistory<string>();
        var before = Snapshot();
        _live["a"] = At(300);
        history.Record("move", before, Snapshot());
        Apply(history.Undo(k => _live[k]));

        before = Snapshot();
        _live["a"] = At(600);
        history.Record("move", before, Snapshot());
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void The_oldest_steps_fall_off_past_the_limit()
    {
        var history = new LayoutHistory<string>(limit: 2);
        for (var x = 1; x <= 3; x++)
        {
            var before = Snapshot();
            _live["a"] = At(x * 100);
            history.Record("move", before, Snapshot());
        }

        Assert.NotNull(history.Undo(k => _live[k]));
        Assert.NotNull(history.Undo(k => _live[k]));
        Assert.Null(history.Undo(k => _live[k]));
    }
}
