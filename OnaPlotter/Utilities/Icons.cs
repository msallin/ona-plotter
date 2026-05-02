namespace OnaPlotter.Utilities;

/// <summary>
/// Inline-SVG icon library. Each entry is the full <c>&lt;svg&gt;</c>
/// element ready to drop into a Razor component via
/// <c>@((MarkupString)Icons.X)</c>. Icons are stroke-based currentColor
/// glyphs that pick up the parent's CSS colour, so a button styled
/// <c>btn-outline-primary</c> renders the icon in the same primary
/// shade as its border.
///
/// <para>Two sizes are exposed: 20×20 (default, map controls + Resources
/// rows) and 14×14 via <see cref="Small"/> (topbar buttons, where a
/// 20-px icon fights the topbar's 32-px-tall row). New consumers
/// reaching for a 14-px icon should prefer the <c>Small*</c>
/// pre-rendered constants over calling <see cref="Render"/> with
/// custom sizes; arbitrary sizes are supported but a non-standard
/// size is usually a sign of a CSS layout issue better fixed at the
/// container.</para>
/// </summary>
public static class Icons
{
    // Inner-path fragments: just the SVG drawing primitives, no
    // <svg> wrapper. Render() / Small() / Default() add the wrapper
    // at the chosen size. Pulled out so the 14-px topbar variants
    // share the same path data as the 20-px map-control variants;
    // a glyph fix lands in one place.
    private const string PathFollow = "<circle cx=\"12\" cy=\"12\" r=\"8\"/><circle cx=\"12\" cy=\"12\" r=\"2\" fill=\"currentColor\"/><path d=\"M12 2v3M12 19v3M2 12h3M19 12h3\"/>";
    private const string PathCompass = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><polygon points=\"12,5 14,13 12,11 10,13\" fill=\"currentColor\" stroke=\"none\"/>";
    private const string PathMoon = "<path d=\"M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z\"/>";
    private const string PathSun = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41\"/>";
    private const string PathAnchor = "<circle cx=\"12\" cy=\"5\" r=\"2\"/><line x1=\"12\" y1=\"7\" x2=\"12\" y2=\"22\"/><line x1=\"8\" y1=\"11\" x2=\"16\" y2=\"11\"/><path d=\"M5 14a7 7 0 0 0 14 0\"/>";
    private const string PathHarbor = "<path d=\"M3 21h18\"/><path d=\"M5 21V11\"/><path d=\"M19 21V11\"/><path d=\"M5 11h14\"/><path d=\"M9 11V5h6v6\"/><path d=\"M12 5V3\"/>";
    private const string PathMob = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 3v3M12 18v3M3 12h3M18 12h3\"/>";
    private const string PathFit = "<path d=\"M9 3H5a2 2 0 0 0-2 2v4\"/><path d=\"M15 3h4a2 2 0 0 1 2 2v4\"/><path d=\"M3 15v4a2 2 0 0 0 2 2h4\"/><path d=\"M21 15v4a2 2 0 0 1-2 2h-4\"/>";
    private const string PathCentre = "<circle cx=\"12\" cy=\"12\" r=\"3\" fill=\"currentColor\"/><path d=\"M12 2v4M12 18v4M2 12h4M18 12h4\"/>";
    private const string PathLaylines = "<path d=\"M12 22 L4 4\"/><path d=\"M12 22 L20 4\"/><circle cx=\"12\" cy=\"22\" r=\"1\" fill=\"currentColor\"/>";
    private const string PathMeasure = "<path d=\"M3 17 L17 3\"/><path d=\"M7 17 L11 13\" stroke-width=\"1.3\"/><path d=\"M13 11 L17 7\" stroke-width=\"1.3\"/><circle cx=\"3\" cy=\"17\" r=\"1.4\" fill=\"currentColor\"/><circle cx=\"17\" cy=\"3\" r=\"1.4\" fill=\"currentColor\"/>";
    private const string PathRoute = "<circle cx=\"5\" cy=\"6\" r=\"2\"/><circle cx=\"19\" cy=\"18\" r=\"2\"/><circle cx=\"12\" cy=\"12\" r=\"1.5\" fill=\"currentColor\"/><path d=\"M7 7 l4 4 M13 13 l4.5 4\" stroke-dasharray=\"3,2\"/>";
    private const string PathFlag = "<path d=\"M4 21V5a2 2 0 0 1 2-2h10l-2 5 2 5H6\"/><line x1=\"4\" y1=\"21\" x2=\"4\" y2=\"13\"/>";
    private const string PathLayers = "<polygon points=\"12,2 22,8.5 12,15 2,8.5\"/><polyline points=\"2,15.5 12,22 22,15.5\"/>";
    private const string PathLegend = "<rect x=\"3\" y=\"4\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"6\" x2=\"21\" y2=\"6\"/><rect x=\"3\" y=\"11\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"13\" x2=\"21\" y2=\"13\"/><rect x=\"3\" y=\"18\" width=\"4\" height=\"4\"/><line x1=\"9\" y1=\"20\" x2=\"21\" y2=\"20\"/>";
    private const string PathMore = "<circle cx=\"5\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/><circle cx=\"12\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/><circle cx=\"19\" cy=\"12\" r=\"1.8\" fill=\"currentColor\"/>";
    private const string PathPlus = "<line x1=\"12\" y1=\"5\" x2=\"12\" y2=\"19\"/><line x1=\"5\" y1=\"12\" x2=\"19\" y2=\"12\"/>";
    private const string PathMinus = "<line x1=\"5\" y1=\"12\" x2=\"19\" y2=\"12\"/>";
    private const string PathUndo = "<path d=\"M3 10h10a5 5 0 0 1 0 10h-2\"/><polyline points=\"7,6 3,10 7,14\"/>";
    private const string PathCheck = "<polyline points=\"20,6 9,17 4,12\"/>";
    private const string PathX = "<line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/><line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>";
    private const string PathFullscreenEnter = "<path d=\"M3 8V5a2 2 0 0 1 2-2h3\"/><path d=\"M16 3h3a2 2 0 0 1 2 2v3\"/><path d=\"M21 16v3a2 2 0 0 1-2 2h-3\"/><path d=\"M8 21H5a2 2 0 0 1-2-2v-3\"/>";
    private const string PathFullscreenExit = "<path d=\"M8 3v4a1 1 0 0 1-1 1H3\"/><path d=\"M21 8h-4a1 1 0 0 1-1-1V3\"/><path d=\"M3 16h4a1 1 0 0 1 1 1v4\"/><path d=\"M16 21v-4a1 1 0 0 1 1-1h4\"/>";
    private const string PathTrash = "<path d=\"M3 6h18\"/><path d=\"M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2\"/><path d=\"M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6\"/><line x1=\"10\" y1=\"11\" x2=\"10\" y2=\"17\"/><line x1=\"14\" y1=\"11\" x2=\"14\" y2=\"17\"/>";
    // Help / question-mark inside a circle. Reads as "help" / "what
    // is this?" -- used by the Tips / Shortcuts More-menu row so
    // it doesn't share a glyph with the Legend row right above.
    private const string PathHelp = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3\"/><line x1=\"12\" y1=\"17\" x2=\"12.01\" y2=\"17\"/>";

    /// <summary>
    /// Build the full <c>&lt;svg&gt;</c> wrapper at an arbitrary
    /// pixel size around the given inner path. Use sparingly; prefer
    /// the pre-rendered <see cref="Default"/> 20-px and
    /// <see cref="Small"/> 14-px constants to keep the icon size
    /// pool small.
    /// </summary>
    public static string Render(string innerPath, int size) =>
        $"<svg width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{innerPath}</svg>";

    // 20-px icons (the original public surface). Existing callers
    // continue to read `Icons.Anchor`, `Icons.Plus`, etc.
    public static readonly string Follow = Render(PathFollow, 20);
    public static readonly string Compass = Render(PathCompass, 20);
    public static readonly string Moon = Render(PathMoon, 20);
    public static readonly string Sun = Render(PathSun, 20);
    public static readonly string Anchor = Render(PathAnchor, 20);
    public static readonly string Harbor = Render(PathHarbor, 20);
    public static readonly string Mob = Render(PathMob, 20);
    public static readonly string Fit = Render(PathFit, 20);
    public static readonly string Centre = Render(PathCentre, 20);
    public static readonly string Laylines = Render(PathLaylines, 20);
    public static readonly string Measure = Render(PathMeasure, 20);
    public static readonly string Route = Render(PathRoute, 20);
    public static readonly string Flag = Render(PathFlag, 20);
    public static readonly string Layers = Render(PathLayers, 20);
    public static readonly string Legend = Render(PathLegend, 20);
    public static readonly string More = Render(PathMore, 20);
    public static readonly string Plus = Render(PathPlus, 20);
    public static readonly string Minus = Render(PathMinus, 20);
    public static readonly string Undo = Render(PathUndo, 20);
    public static readonly string Check = Render(PathCheck, 20);
    public static readonly string X = Render(PathX, 20);
    public static readonly string FullscreenEnter = Render(PathFullscreenEnter, 20);
    public static readonly string FullscreenExit = Render(PathFullscreenExit, 20);
    public static readonly string Trash = Render(PathTrash, 20);
    public static readonly string Help = Render(PathHelp, 20);

    /// <summary>
    /// 14-px variants for the topbar row, where the surrounding chrome
    /// is shorter than the map-control bar's 36-px buttons. Lets the
    /// helm read the topbar at a glance without the icons crowding
    /// the row's vertical breathing room.
    /// </summary>
    public static class Small
    {
        public static readonly string Plus = Render(PathPlus, 14);
        public static readonly string Minus = Render(PathMinus, 14);
        public static readonly string Moon = Render(PathMoon, 14);
        public static readonly string Sun = Render(PathSun, 14);
        public static readonly string FullscreenEnter = Render(PathFullscreenEnter, 14);
        public static readonly string FullscreenExit = Render(PathFullscreenExit, 14);
    }
}
