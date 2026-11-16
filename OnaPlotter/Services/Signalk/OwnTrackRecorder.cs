using OnaPlotter.Models;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Samples the own-boat trail into <see cref="TrackBuffer"/> as self
/// data changes. TrackBuffer holds 1000 points; one per delta (1 Hz
/// typical, bursting to 3-5 Hz under heavy wind / depth traffic) would
/// fill it in 15-30 minutes of sailing. One point every 5 s stretches
/// it to ~83 minutes, enough to follow a typical day-sail. The live
/// position indicator still updates on every delta; only the persisted
/// trail is throttled.
/// </summary>
internal sealed class OwnTrackRecorder
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private readonly NavigationData _data;
    private readonly TrackBuffer _track;
    private readonly TimeProvider _time;
    private long _lastSampleTicks;

    public OwnTrackRecorder(NavigationData data, TrackBuffer track, TimeProvider time)
    {
        _data = data;
        _track = track;
        _time = time;
    }

    /// <summary>Called after a self delta changed
    /// <see cref="NavigationData"/>. Records a point when there is a fix
    /// and at least <see cref="SampleInterval"/> passed since the last
    /// one.</summary>
    public void OnSelfDataChanged()
    {
        if (_data.Latitude is not double lat || _data.Longitude is not double lon) return;

        var now = _time.GetUtcNow();
        long lastTicks = Interlocked.Read(ref _lastSampleTicks);
        if (now.UtcTicks - lastTicks < SampleInterval.Ticks) return;
        Interlocked.Exchange(ref _lastSampleTicks, now.UtcTicks);

        _track.Add(new TrackPoint(
            now.UtcDateTime,
            lat,
            lon,
            _data.SpeedOverGround,
            _data.CourseOverGround,
            _data.Heading,
            _data.WindAngleApparent,
            _data.WindSpeedApparent,
            _data.WindAngleTrue,
            _data.WindSpeedTrue,
            _data.Depth,
            _data.WindDirectionTrue));
    }
}
