using Flamingle.Core.Geometry;
using Flamingle.Core.Saved;

namespace Flamingle.Tests;

public class RelaunchLogicTests
{
    private static SavedWindow Saved(string exe = @"C:\Apps\Code.exe", string cls = "Chrome_WidgetWin_1", string title = "app - Code",
        bool packaged = false, string? aumid = null, LaunchRecipe? recipe = null) => new(
        Path.GetFileNameWithoutExtension(exe), packaged ? null : exe, "", aumid, packaged, cls, title,
        new SavedPlacement(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1040), 1.0, new PixelRect(0, 0, 800, 600), null, false, false),
        0, Recipe: recipe);

    private static PlannedWindow Plan(SavedWindow w, string? titleContains = null)
    {
        var flock = new SavedFlock(Guid.NewGuid(), "f", DateTimeOffset.UnixEpoch, [w]);
        var p = LoadPlan.Build(flock, new Dictionary<string, string>()).Windows[0];
        return p with { TitleContains = titleContains };
    }

    private static OpenWindow Open(long h, string? exe = @"C:\Apps\Code.exe", string title = "x - Code", string cls = "Chrome_WidgetWin_1",
        string? aumid = null) => new(h, exe, aumid, cls, title);

    [Fact]
    public void Matches_by_exe_whatever_the_case_and_by_title_when_asked()
    {
        var p = Plan(Saved(), titleContains: "flamingle");
        Assert.True(WindowMatcher.Matches(p, Open(1, @"c:\apps\code.EXE", "flamingle - Visual Studio Code")));
        Assert.False(WindowMatcher.Matches(p, Open(2, title: "other - Code")));
        Assert.False(WindowMatcher.Matches(p, Open(3, @"C:\Apps\Other.exe", "flamingle")));
        Assert.False(WindowMatcher.Matches(p, Open(4, exe: null, title: "flamingle")));
    }

    [Fact]
    public void Store_apps_match_by_app_id_and_edited_launchers_still_match_the_captured_app()
    {
        var terminal = Saved(packaged: true, aumid: "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", cls: "CASCADIA_HOSTING_WINDOW_CLASS");
        Assert.True(WindowMatcher.IsApp(Plan(terminal), Open(1, @"C:\Program Files\WindowsApps\wt\WindowsTerminal.exe", aumid: "microsoft.windowsterminal_8wekyb3d8bbwe!app")));

        // Started through wt.exe, but the window belongs to the captured app.
        var viaLauncher = Saved(@"C:\Apps\WindowsTerminal.exe", recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\wt.exe", "-d x"));
        Assert.True(WindowMatcher.IsApp(Plan(viaLauncher), Open(2, @"C:\Apps\WindowsTerminal.exe")));
        Assert.True(WindowMatcher.IsApp(Plan(viaLauncher), Open(3, @"C:\Apps\wt.exe")));
    }

    [Fact]
    public void Best_prefers_the_captured_class_then_title_then_the_topmost()
    {
        var p = Plan(Saved(title: "app - Code"));
        OpenWindow[] candidates =
        [
            Open(1, cls: "Popup"),
            Open(2, title: "other - Code"),
            Open(3, title: "app - Code"),
            Open(4, @"C:\Apps\Other.exe", "app - Code"),
        ];
        Assert.Equal(3, WindowMatcher.Best(p, candidates)!.Handle);
        Assert.Equal(2, WindowMatcher.Best(p, candidates.Where(c => c.Handle != 3))!.Handle);
        Assert.Null(WindowMatcher.Best(p, [candidates[3]]));
    }

    [Fact]
    public void A_window_settles_once_unchanged_titled_and_big_enough()
    {
        var time = new ManualTime();
        var settler = new WindowSettler(time);
        var big = new PixelRect(0, 0, 800, 600);

        Assert.False(settler.Observe(1, "Loading", new PixelRect(0, 0, 300, 200)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(settler.Observe(1, "app - Code", big));     // changed: the clock restarts
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(settler.Observe(1, "app - Code", big));
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(settler.Observe(1, "app - Code", big));

        Assert.False(settler.Observe(2, "Splash", new PixelRect(0, 0, 150, 90)));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(settler.Observe(2, "Splash", new PixelRect(0, 0, 150, 90)));   // too small to be the window
    }

    [Fact]
    public void Monitors_map_by_device_then_size_then_primary()
    {
        var primary = new MonitorInfo(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 1.0, true);
        var side = new MonitorInfo(2, @"\\.\DISPLAY3", new PixelRect(2560, 0, 1920, 1080), new PixelRect(2560, 0, 1920, 1040), 1.0, false);
        MonitorInfo[] monitors = [primary, side];

        Assert.Same(side, PlacementMapper.Monitor(@"\\.\DISPLAY3", default, monitors));
        Assert.Same(side, PlacementMapper.Monitor(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 1920, 1040), monitors));
        Assert.Same(primary, PlacementMapper.Monitor(@"\\.\DISPLAY9", new PixelRect(0, 0, 1280, 1024), monitors));
    }

    [Fact]
    public void Rects_stay_put_when_nothing_changed_and_scale_onto_a_different_work_area()
    {
        var work = new PixelRect(0, 0, 2000, 1000);
        var rect = new PixelRect(-7, 0, 1014, 1007);   // invisible borders poke out — left alone when nothing changed
        Assert.Equal(rect, PlacementMapper.Rect(rect, work, work));

        var half = new PixelRect(1000, 0, 1000, 1000);
        Assert.Equal(new PixelRect(2500, 0, 500, 500), PlacementMapper.Rect(half, work, new PixelRect(2000, 0, 1000, 500)));

        // Bigger than the new work area: shrunk to it and kept on screen.
        Assert.Equal(new PixelRect(0, 0, 800, 600), PlacementMapper.Rect(new PixelRect(0, 0, 2000, 1000), new PixelRect(0, 0, 0, 0), new PixelRect(0, 0, 800, 600)));
    }
}
