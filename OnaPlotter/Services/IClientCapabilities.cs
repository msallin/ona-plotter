namespace OnaPlotter.Services;

/// <summary>
/// Environment-derived capabilities of the client device. Consumed by
/// the Map page (and AIS layer) to gate perf-heavy effects like SVG
/// drop-shadows / detectRetina tile fetches / synced AIS updates
/// during a drag. These are NOT user preferences - they're detected
/// once at startup from <c>navigator</c> and stay fixed for the
/// session, so they live in their own service rather than IAppSettings.
///
/// <para>The detection moved from JS to C# so any decision based on
/// the flag (which Leaflet renderer to use, whether to push AIS
/// during a pan) sits on the C# side where the rest of the rendering
/// and alarm logic lives. JS just receives the resolved boolean.</para>
/// </summary>
public interface IClientCapabilities
{
    /// <summary>True for low-power devices (Raspberry Pi, ARM tablet,
    /// any client reporting <c>navigator.hardwareConcurrency &lt;= 4</c>).
    /// Drives Leaflet's preferCanvas, tile updateWhenIdle, detectRetina,
    /// and the AIS layer's "skip pushes during a drag" gate.
    /// <para>Defaults to <c>false</c> until <see cref="InitializeAsync"/>
    /// runs, so a pre-bootstrap consumer reads the desktop-class
    /// behaviour and a slow Pi only loses one or two render frames
    /// before the flag is correct.</para></summary>
    bool IsSlowClient { get; }

    /// <summary>One-shot bootstrap. Pulls
    /// <c>navigator.hardwareConcurrency</c> + <c>navigator.userAgent</c>
    /// via JS interop and resolves <see cref="IsSlowClient"/>. Safe to
    /// call multiple times; subsequent calls re-evaluate (cheap).</summary>
    Task InitializeAsync();
}
