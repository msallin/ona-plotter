namespace OnaPlotter.Utilities;

/// <summary>
/// Chart-ready view of a rolling series over a trailing window: a
/// stride-decimated vertex list for the polyline plus min / max / mean
/// over EVERY in-window sample, so axis bounds and the mean line are not
/// skewed by the decimation. Built in one pass straight from the series'
/// storage by <see cref="RollingScalarSeries.ChartIn"/> /
/// <see cref="RollingDirectionSeries.ChartIn"/>, so a 2 h window of 1 Hz
/// samples costs ~600 vertex allocations instead of a 7,200-entry copy
/// per render.
/// </summary>
/// <param name="Vertices">Oldest to newest; first and last in-window
/// samples always included so the line spans the full window.</param>
/// <param name="Count">Number of in-window samples the stats cover.</param>
public sealed record ChartSeries(
    IReadOnlyList<TimeSeriesSample> Vertices, int Count, double Min, double Max, double Mean)
{
    public static ChartSeries Empty { get; } = new([], 0, 0, 0, 0);

    /// <summary>Time between the oldest and newest in-window sample.</summary>
    public TimeSpan Span => Vertices.Count < 2 ? TimeSpan.Zero : Vertices[^1].Time - Vertices[0].Time;
}

/// <summary>
/// Single-pass accumulator behind <see cref="ChartSeries"/>. The caller
/// knows the in-window sample count up front, which fixes the stride:
/// count 1,500 with maxVertices 600 gives stride 3, emitting samples
/// 0, 3, 6, ... plus the last one.
/// </summary>
internal struct ChartSeriesBuilder
{
    private readonly int _count;
    private readonly int _stride;
    private readonly List<TimeSeriesSample> _vertices;
    private int _index;
    private double _min, _max, _sum;

    public ChartSeriesBuilder(int count, int maxVertices)
    {
        if (maxVertices < 2)
            throw new ArgumentOutOfRangeException(nameof(maxVertices), maxVertices, "Need at least two vertices.");
        _count = count;
        _stride = count > maxVertices ? (int)Math.Ceiling(count / (double)maxVertices) : 1;
        _vertices = new List<TimeSeriesSample>(Math.Min(count, maxVertices + 1));
        _index = 0;
        _min = double.MaxValue;
        _max = double.MinValue;
        _sum = 0;
    }

    public void Add(DateTime time, double value)
    {
        if (value < _min) _min = value;
        if (value > _max) _max = value;
        _sum += value;
        if (_index == 0 || _index == _count - 1 || _index % _stride == 0)
            _vertices.Add(new TimeSeriesSample(time, value));
        _index++;
    }

    public readonly ChartSeries Build() =>
        _index == 0 ? ChartSeries.Empty : new ChartSeries(_vertices, _index, _min, _max, _sum / _index);
}
