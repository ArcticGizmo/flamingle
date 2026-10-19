using Avalonia;
using Flamingle.App.Native;
using Flamingle.Core;
using Flamingle.Core.Grouping;

namespace Flamingle.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-
    // reliant code before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless diagnostic for tuning the snap-group heuristic: list the candidate windows and every
        // group the detector finds. Pipe it somewhere to see it (a WinExe has no console of its own):
        //   .\flamingle.exe windows | Out-String
        if (args.Length > 0 && string.Equals(args[0], "windows", StringComparison.OrdinalIgnoreCase))
            return RunListWindows();

        // One resident Flamingle per session (a dev build gets its own, so it runs beside an installed one).
        using var mutex = new Mutex(initiallyOwned: true,
            @"Local\Flamingle.SingleInstance.v1" + (AppProfile.IsDev ? ".Dev" : ""), out var createdNew);
        if (!createdNew) return 0;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        return 0;
    }

    private static int RunListWindows()
    {
        Console.WriteLine("Monitors:");
        foreach (var m in Monitors.All())
            Console.WriteLine($"  {m.ShortName,-12} {m.Bounds.Width}x{m.Bounds.Height} @ {m.Bounds.X},{m.Bounds.Y}  key {m.DeviceName}");

        var windows = WindowQuery.Candidates();
        Console.WriteLine($"{windows.Count} candidate window(s), topmost first:");
        foreach (var w in windows)
        {
            var state = w.IsMinimised ? " [min]" : w.IsMaximised ? " [max]" : "";
            Console.WriteLine(
                $"  0x{w.Handle:X8}  {w.Bounds.Width,5}x{w.Bounds.Height,-5} @ {w.Bounds.X,6},{w.Bounds.Y,-6} " +
                $"mon {w.Monitor:X}{state}  {w.ProcessName}: {w.Title}");
        }

        var seen = new HashSet<string>();
        Console.WriteLine("Snap groups (2+ windows):");
        foreach (var w in windows)
        {
            var group = SnapGroupDetector.Detect(w.Handle, windows);
            if (group.Count < 2) continue;
            var key = string.Join(",", group.Order());
            if (!seen.Add(key)) continue;
            var names = group.Select(h => windows.First(x => x.Handle == h).ProcessName);
            Console.WriteLine($"  {string.Join(" + ", names)}");
        }
        if (seen.Count == 0) Console.WriteLine("  (none)");
        return 0;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
