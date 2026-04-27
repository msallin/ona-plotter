namespace OnaPlotter.Utilities;

public static class Icons
{
    private const string Wrap = "<svg width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";
    private const string End = "</svg>";

    // Follow / lock-on (crosshair with dot).
    public const string Follow = Wrap + "<circle cx=\"12\" cy=\"12\" r=\"8\"/><circle cx=\"12\" cy=\"12\" r=\"2\" fill=\"currentColor\"/><path d=\"M12 2v3M12 19v3M2 12h3M19 12h3\"/>" + End;

    // Compass rose with N arrow.
    public const string Compass = Wrap + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><polygon points=\"12,5 14,13 12,11 10,13\" fill=\"currentColor\" stroke=\"none\"/>" + End;

    // Moon (night mode).
    public const string Moon = Wrap + "<path d=\"M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z\"/>" + End;

    // Anchor.
    public const string Anchor = Wrap + "<circle cx=\"12\" cy=\"5\" r=\"2\"/><line x1=\"12\" y1=\"7\" x2=\"12\" y2=\"22\"/><line x1=\"8\" y1=\"11\" x2=\"16\" y2=\"11\"/><path d=\"M5 14a7 7 0 0 0 14 0\"/>" + End;

    // Harbor: simplified pier-with-crane silhouette. Pier deck on
    // the waterline, two pilings at the ends, and a small lift hut
    // with a vertical mast on top. Reads as "in port" without
    // depending on a full skyline.
    public const string Harbor = Wrap + "<path d=\"M3 21h18\"/><path d=\"M5 21V11\"/><path d=\"M19 21V11\"/><path d=\"M5 11h14\"/><path d=\"M9 11V5h6v6\"/><path d=\"M12 5V3\"/>" + End;

    // MOB: person in distress / life ring.
    public const string Mob = Wrap + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 3v3M12 18v3M3 12h3M18 12h3\"/>" + End;

    // Fit bounds: four corners pointing inward.
    public const string Fit = Wrap + "<path d=\"M9 3H5a2 2 0 0 0-2 2v4\"/><path d=\"M15 3h4a2 2 0 0 1 2 2v4\"/><path d=\"M3 15v4a2 2 0 0 0 2 2h4\"/><path d=\"M21 15v4a2 2 0 0 1-2 2h-4\"/>" + End;

    // Centre-on-boat: crosshair + dot. One-shot "pan here" vs the Follow
    // icon which implies continuous lock.
    public const string Centre = Wrap + "<circle cx=\"12\" cy=\"12\" r=\"3\" fill=\"currentColor\"/><path d=\"M12 2v4M12 18v4M2 12h4M18 12h4\"/>" + End;

    // Laylines: two diverging lines from bottom.
    public const string Laylines = Wrap + "<path d=\"M12 22 L4 4\"/><path d=\"M12 22 L20 4\"/><circle cx=\"12\" cy=\"22\" r=\"1\" fill=\"currentColor\"/>" + End;

    // Measure: ruler-ish diagonal with two tick marks.
    public const string Measure = Wrap + "<path d=\"M3 17 L17 3\"/><path d=\"M7 17 L11 13\" stroke-width=\"1.3\"/><path d=\"M13 11 L17 7\" stroke-width=\"1.3\"/><circle cx=\"3\" cy=\"17\" r=\"1.4\" fill=\"currentColor\"/><circle cx=\"17\" cy=\"3\" r=\"1.4\" fill=\"currentColor\"/>" + End;

    // Route: connected waypoints.
    public const string Route = Wrap + "<circle cx=\"5\" cy=\"6\" r=\"2\"/><circle cx=\"19\" cy=\"18\" r=\"2\"/><circle cx=\"12\" cy=\"12\" r=\"1.5\" fill=\"currentColor\"/><path d=\"M7 7 l4 4 M13 13 l4.5 4\" stroke-dasharray=\"3,2\"/>" + End;

    // Flag (race timer).
    public const string Flag = Wrap + "<path d=\"M4 21V5a2 2 0 0 1 2-2h10l-2 5 2 5H6\"/><line x1=\"4\" y1=\"21\" x2=\"4\" y2=\"13\"/>" + End;

    // Layers (stacked).
    public const string Layers = Wrap + "<polygon points=\"12,2 22,8.5 12,15 2,8.5\"/><polyline points=\"2,15.5 12,22 22,15.5\"/>" + End;

    // Legend: coloured swatches next to short lines, reads as "list + key".
    public const string Legend = Wrap + "<rect x=\"3\" y=\"4\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"6\" x2=\"21\" y2=\"6\"/><rect x=\"3\" y=\"11\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"13\" x2=\"21\" y2=\"13\"/><rect x=\"3\" y=\"18\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"20\" x2=\"21\" y2=\"20\"/>" + End;

    // More: three horizontal dots, universal overflow/menu glyph.
    public const string More = Wrap + "<circle cx=\"5\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/><circle cx=\"12\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/><circle cx=\"19\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/>" + End;

    // Plus (add waypoint, etc.)
    public const string Plus = Wrap + "<line x1=\"12\" y1=\"5\" x2=\"12\" y2=\"19\"/><line x1=\"5\" y1=\"12\" x2=\"19\" y2=\"12\"/>" + End;

    // Undo.
    public const string Undo = Wrap + "<path d=\"M3 10h10a5 5 0 0 1 0 10h-2\"/><polyline points=\"7,6 3,10 7,14\"/>" + End;

    // Check (save).
    public const string Check = Wrap + "<polyline points=\"20,6 9,17 4,12\"/>" + End;

    // X (cancel).
    public const string X = Wrap + "<line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/><line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>" + End;
}
