namespace Gaggle.Core.Zones;

/// <summary>A zone as fractions (0..1) of a monitor's work area, so one layout fits any monitor.</summary>
public readonly record struct ZoneFraction(double X, double Y, double Width, double Height);

/// <summary>
/// A named arrangement of zones, FancyZones-style. Zones are fractions of the work area; spacing between
/// them (and around the edge) is applied in physical pixels when the layout is resolved for a monitor.
/// </summary>
public sealed record ZoneLayout(string Id, string Name, IReadOnlyList<ZoneFraction> Zones)
{
    /// <summary>
    /// The built-in templates. No editor yet — while we prove the interaction out, these cover the common
    /// shapes. Ids are what <c>settings.json</c> stores per monitor.
    /// </summary>
    public static IReadOnlyList<ZoneLayout> Templates { get; } =
    [
        Columns("columns-2", "2 columns", 2),
        Columns("columns-3", "3 columns", 3),
        Columns("columns-4", "4 columns", 4),
        Grid("grid-2x2", "2 × 2 grid", rows: 2, cols: 2),
        Grid("grid-2x3", "2 × 3 grid", rows: 2, cols: 3),
        Grid("grid-3x3", "3 × 3 grid", rows: 3, cols: 3),
        Rows("rows-2", "2 rows", 2),
        Rows("rows-3", "3 rows", 3),
        new("priority", "Priority (25 / 50 / 25)",
        [
            new(0, 0, 0.25, 1), new(0.25, 0, 0.5, 1), new(0.75, 0, 0.25, 1),
        ]),
        new("main-stack", "Main + side stack (60 / 40)",
        [
            new(0, 0, 0.6, 1), new(0.6, 0, 0.4, 0.5), new(0.6, 0.5, 0.4, 0.5),
        ]),
        new("stack-main", "Side stack + main (40 / 60)",
        [
            new(0, 0, 0.4, 0.5), new(0, 0.5, 0.4, 0.5), new(0.4, 0, 0.6, 1),
        ]),
        new("big-2x2", "Half + 2 × 2",
        [
            new(0, 0, 0.5, 1),
            new(0.5, 0, 0.25, 0.5), new(0.75, 0, 0.25, 0.5),
            new(0.5, 0.5, 0.25, 0.5), new(0.75, 0.5, 0.25, 0.5),
        ]),
        new("wide-centre", "Wide centre + corners",
        [
            new(0, 0, 0.2, 0.5), new(0, 0.5, 0.2, 0.5),
            new(0.2, 0, 0.6, 1),
            new(0.8, 0, 0.2, 0.5), new(0.8, 0.5, 0.2, 0.5),
        ]),
        new("focus", "Focus (one centred window)",
        [
            new(0.15, 0.05, 0.7, 0.9),
        ]),
    ];

    public static string DefaultId => "columns-2";

    public static ZoneLayout ById(string? id)
        => Templates.FirstOrDefault(t => t.Id == id) ?? Templates.First(t => t.Id == DefaultId);

    public static ZoneLayout Columns(string id, string name, int count)
        => Grid(id, name, rows: 1, cols: count);

    public static ZoneLayout Rows(string id, string name, int count)
        => Grid(id, name, rows: count, cols: 1);

    public static ZoneLayout Grid(string id, string name, int rows, int cols)
    {
        var zones = new List<ZoneFraction>(rows * cols);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                zones.Add(new ZoneFraction((double)c / cols, (double)r / rows, 1.0 / cols, 1.0 / rows));
        return new ZoneLayout(id, name, zones);
    }
}
