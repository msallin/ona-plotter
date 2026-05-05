using OnaPlotter.Utilities;

namespace OnaPlotter.Services;

/// <summary>
/// Single source of truth for time-windowed means and stats over the
/// chartplotter's nav channels. Holds one rolling buffer per channel
/// at a retention long enough to satisfy every consumer (HUD,
/// WindRose chips, future trend chips, ...) so the same sample
/// stream backs both the HUD's fixed 30 s SOG mean and a helm-picked
/// 60 min wind-stats query on the WindRose page -- no parallel
/// buffers, no drift between renderers.
///
/// <para>Convenience properties expose the canonical windows the HUD
/// renders today; <see cref="Tws"/> and friends expose the underlying
/// <see cref="RollingScalarSeries"/> directly so callers that need
/// other windows (WindRose's helm-picked chip, future trend chips)
/// can query the same buffer.</para>
/// </summary>
public interface INavigationAverages
{
    // ---- Underlying buffers (variable-window queries) ----------

    /// <summary>True wind speed (m/s) -- 60 min retention.</summary>
    RollingScalarSeries Tws { get; }
    /// <summary>Apparent wind speed (m/s) -- 60 min retention.</summary>
    RollingScalarSeries Aws { get; }
    /// <summary>True wind direction (radians) -- 60 min retention.</summary>
    RollingDirectionSeries Twd { get; }
    /// <summary>Speed over ground (m/s) -- 5 min retention.</summary>
    RollingScalarSeries Sog { get; }
    /// <summary>VMG to next waypoint (m/s) -- 5 min retention.</summary>
    RollingScalarSeries Vmg { get; }
    /// <summary>Course over ground (radians) -- 5 min retention.</summary>
    RollingDirectionSeries Cog { get; }

    // ---- Convenience properties (canonical HUD windows) --------

    /// <summary>1-minute mean true wind speed (m/s); null until 50%
    /// of the window is covered.</summary>
    double? TwsMean1Min { get; }
    /// <summary>10-minute mean true wind speed (m/s).</summary>
    double? TwsMean10Min { get; }
    /// <summary>1-minute mean apparent wind speed (m/s).</summary>
    double? AwsMean1Min { get; }
    /// <summary>10-minute mean apparent wind speed (m/s).</summary>
    double? AwsMean10Min { get; }
    /// <summary>30-second mean SOG (m/s) -- wave-noise smoothing.</summary>
    double? SogMean30Sec { get; }
    /// <summary>60-second mean VMG (m/s) -- gust-bounce smoothing.</summary>
    double? VmgMean1Min { get; }
    /// <summary>30-second circular mean COG (radians, [-π, +π]).
    /// Null when SOG has been below the stationary threshold for
    /// the entire window (direction is meaningless when not moving).</summary>
    double? CogMean30Sec { get; }
}
