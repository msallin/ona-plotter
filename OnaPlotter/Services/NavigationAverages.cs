using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services;

/// <summary>
/// Default <see cref="INavigationAverages"/>. Subscribes to
/// <see cref="SignalkClient.OnDataChanged"/>; on each tick samples
/// the live values from <see cref="SignalkClient.Data"/> and feeds
/// them into the rolling buffers below.
///
/// <para>One singleton for the whole app - buffers live for the
/// lifetime of the WASM process. Disposed at host shutdown so the
/// event subscription doesn't leak across hot-reloads in dev.</para>
/// </summary>
public sealed class NavigationAverages : INavigationAverages, IDisposable
{
    /// <summary>SOG threshold below which COG samples are dropped
    /// (~0.5 kn). When a boat is drifting in a current with the
    /// engine off, the GPS noise floor produces COG that flips
    /// 360° per fix; including those poisons the rolling mean.</summary>
    public const double StationarySogMs = 0.25;

    private readonly SignalkClient _client;
    private readonly TimeProvider _time;
    private bool _disposed;

    public RollingScalarSeries Tws { get; }
    public RollingScalarSeries Aws { get; }
    public RollingDirectionSeries Twd { get; }
    public RollingDirectionSeries Awa { get; }
    public RollingDirectionSeries Twa { get; }
    public RollingScalarSeries Sog { get; }
    public RollingScalarSeries Vmg { get; }
    public RollingDirectionSeries Cog { get; }
    /// <summary>Per-axis COG buffers. Sample directly off the raw
    /// <c>CourseOverGroundTrue</c> / <c>CourseOverGroundMagnetic</c>
    /// fields so the helm's CogReadoutSource / OwnCogVectorSource
    /// pick can mix true-smoothed and magnetic-smoothed without one
    /// buffer's pre-flip samples poisoning the other after a pick
    /// change. The legacy combined <see cref="Cog"/> buffer is kept
    /// for the back-compat <see cref="CogMean30Sec"/> path.</summary>
    public RollingDirectionSeries CogTrue { get; }
    public RollingDirectionSeries CogMagnetic { get; }

    public NavigationAverages(SignalkClient client, TimeProvider? time = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? TimeProvider.System;

        // Wind: 180 min retention - covers WindRose's longest
        // history window (3 h) and its 60-min chip query, plus the
        // HUD's 1-min and 10-min canonical means as sub-windows.
        Tws = new RollingScalarSeries(TimeSpan.FromMinutes(180), _time);
        Aws = new RollingScalarSeries(TimeSpan.FromMinutes(180), _time);
        Twd = new RollingDirectionSeries(TimeSpan.FromMinutes(180), _time);

        // Boat motion: 5 min retention. The HUD wants 30 s smoothing;
        // 5 min leaves room for a future "what's my last 5 min mean"
        // chip without re-instrumenting the sampler.
        Sog = new RollingScalarSeries(TimeSpan.FromMinutes(5), _time);
        Vmg = new RollingScalarSeries(TimeSpan.FromMinutes(5), _time);
        Cog = new RollingDirectionSeries(TimeSpan.FromMinutes(5), _time);
        // Per-axis: same retention as the combined buffer; sampled
        // off the raw True / Magnetic fields independently of the
        // helm's PreferMagneticCourse pick.
        CogTrue = new RollingDirectionSeries(TimeSpan.FromMinutes(5), _time);
        CogMagnetic = new RollingDirectionSeries(TimeSpan.FromMinutes(5), _time);

        // Bow-relative wind angles (AWA / TWA): 5 min retention so
        // they share the boat-motion lifetime; the HUD pulls a 30 s
        // mean to keep the dial arrows from twitching on every gust.
        Awa = new RollingDirectionSeries(TimeSpan.FromMinutes(5), _time);
        Twa = new RollingDirectionSeries(TimeSpan.FromMinutes(5), _time);

        _client.OnDataChanged += HandleDataChanged;
    }

    /// <summary>Test seam: feed a NavigationData snapshot directly
    /// without going through SignalkClient. Production callers
    /// should not use this - the OnDataChanged subscription
    /// handles sampling automatically.</summary>
    internal void SampleForTest(NavigationData data) => Sample(data);

    // ---- Convenience props -------------------------------------

    public double? TwsMean1Min => Tws.Mean(TimeSpan.FromMinutes(1));
    public double? TwsMean10Min => Tws.Mean(TimeSpan.FromMinutes(10));
    public double? AwsMean1Min => Aws.Mean(TimeSpan.FromMinutes(1));
    public double? AwsMean10Min => Aws.Mean(TimeSpan.FromMinutes(10));
    public double? SogMean30Sec => Sog.Mean(TimeSpan.FromSeconds(30));
    public double? VmgMean1Min => Vmg.Mean(TimeSpan.FromMinutes(1));
    public double? CogMean30Sec => Cog.Mean(TimeSpan.FromSeconds(30));
    public double? CogTrueMean30Sec => CogTrue.Mean(TimeSpan.FromSeconds(30));
    public double? CogMagneticMean30Sec => CogMagnetic.Mean(TimeSpan.FromSeconds(30));
    public double? AwaMean30Sec => Awa.Mean(TimeSpan.FromSeconds(30));
    public double? TwaMean30Sec => Twa.Mean(TimeSpan.FromSeconds(30));

    // ---- Sampling pipeline -------------------------------------

    private void HandleDataChanged()
    {
        if (_disposed) return;
        Sample(_client.Data);
    }

    private void Sample(NavigationData d)
    {
        if (d.WindSpeedTrue is double tws) Tws.Add(tws);
        if (d.WindSpeedApparent is double aws) Aws.Add(aws);
        if (d.WindDirectionTrue is double twd) Twd.Add(twd);
        if (d.WindAngleApparent is double awa) Awa.Add(awa);
        if (d.WindAngleTrue is double twa) Twa.Add(twa);
        if (d.SpeedOverGround is double sog)
        {
            Sog.Add(sog);
            // COG only contributes when actually moving - zero-weight
            // sample below the stationary threshold so the buffer
            // entry exists (for warmup coverage) but doesn't pull
            // the mean toward the GPS-noise direction. Same weight
            // applied to all three buffers (combined + per-axis).
            var weight = sog >= StationarySogMs ? 1.0 : 0.0;
            if (d.CourseOverGround is double cog) Cog.Add(cog, weight);
            if (d.CourseOverGroundTrue is double cogTrue) CogTrue.Add(cogTrue, weight);
            if (d.CourseOverGroundMagnetic is double cogMag) CogMagnetic.Add(cogMag, weight);
        }
        if (d.CourseNextPointVmg is double vmg) Vmg.Add(vmg);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.OnDataChanged -= HandleDataChanged;
    }
}
