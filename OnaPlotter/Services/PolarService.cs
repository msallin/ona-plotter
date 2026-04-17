using System.Globalization;

namespace OnaPlotter.Services;

/// <summary>
/// Boat polar performance table (TWA x TWS -> target boat speed) with
/// bilinear interpolation. Persists the raw CSV to <see cref="IKeyValueStore"/>.
/// </summary>
/// <remarks>
/// CSV format: first row is TWS headers (TWA, 6, 8, 10, ...); each
/// subsequent row starts with a TWA value followed by target BSP per TWS.
/// Separator auto-detected: comma, semicolon, or tab. Decimals must be '.'.
/// </remarks>
public sealed class PolarService : IPolarService
{
    private const string StorageKey = "polar";

    private readonly IKeyValueStore _store;

    private double[] _tws = [];       // TWS header values (knots), ascending.
    private double[] _twa = [];       // TWA row values (degrees), ascending.
    private double[,] _speeds = new double[0, 0];
    private bool _loaded;

    public bool HasPolar => _twa.Length > 0 && _tws.Length > 0;
    public double[] TwsValues => _tws;
    public double[] TwaValues => _twa;

    public event Action? OnPolarChanged;

    public PolarService(IKeyValueStore store) => _store = store;

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var csv = await _store.GetAsync(StorageKey);
            if (!string.IsNullOrEmpty(csv)) Parse(csv);
        }
        catch (Microsoft.JSInterop.JSException)
        {
            // Storage unavailable - start empty. Not an error.
        }
    }

    public async Task ImportAsync(string csv)
    {
        Parse(csv);
        await _store.SetAsync(StorageKey, csv);
        OnPolarChanged?.Invoke();
    }

    public async Task ClearAsync()
    {
        _tws = [];
        _twa = [];
        _speeds = new double[0, 0];
        await _store.RemoveAsync(StorageKey);
        OnPolarChanged?.Invoke();
    }

    public double? GetTargetSpeed(double twaDeg, double twsKn)
    {
        if (!HasPolar) return null;

        double absTwa = Math.Abs(twaDeg);
        if (absTwa > 180) absTwa = 360 - absTwa;

        var (twaLo, twaHi, twaFrac) = Bracket(_twa, absTwa);
        var (twsLo, twsHi, twsFrac) = Bracket(_tws, twsKn);

        double s00 = _speeds[twaLo, twsLo];
        double s01 = _speeds[twaLo, twsHi];
        double s10 = _speeds[twaHi, twsLo];
        double s11 = _speeds[twaHi, twsHi];

        double top = s00 + (s01 - s00) * twsFrac;
        double bot = s10 + (s11 - s10) * twsFrac;
        return top + (bot - top) * twaFrac;
    }

    public double? GetPerformance(double sogKn, double twaDeg, double twsKn)
    {
        var target = GetTargetSpeed(twaDeg, twsKn);
        if (target is null || target <= 0) return null;
        return sogKn / target.Value;
    }

    public (double Twa, double Speed)[] GetPolarCurve(double twsKn)
    {
        if (!HasPolar) return [];
        var result = new (double, double)[_twa.Length];
        for (int i = 0; i < _twa.Length; i++)
            result[i] = (_twa[i], GetTargetSpeed(_twa[i], twsKn) ?? 0);
        return result;
    }

    // Finds bracketing indices + interpolation fraction. Clamps to edges if out-of-range.
    private static (int lo, int hi, double frac) Bracket(double[] axis, double v)
    {
        if (v <= axis[0]) return (0, 0, 0);
        if (v >= axis[^1]) return (axis.Length - 1, axis.Length - 1, 0);
        for (int i = 0; i < axis.Length - 1; i++)
        {
            if (axis[i] <= v && axis[i + 1] >= v)
            {
                double span = axis[i + 1] - axis[i];
                return (i, i + 1, span > 0 ? (v - axis[i]) / span : 0);
            }
        }
        return (axis.Length - 1, axis.Length - 1, 0);
    }

    private void Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            throw new FormatException("Polar CSV requires a header row and at least one data row.");

        var header = Split(lines[0]);
        if (header.Length < 2)
            throw new FormatException("Polar CSV header must include at least TWA and one TWS column.");

        var tws = new double[header.Length - 1];
        for (int i = 1; i < header.Length; i++)
        {
            if (!double.TryParse(header[i], NumberStyles.Float, CultureInfo.InvariantCulture, out tws[i - 1]))
                throw new FormatException($"Invalid TWS value in header: '{header[i]}'.");
        }

        var twaList = new List<double>(lines.Length - 1);
        var speedRows = new List<double[]>(lines.Length - 1);

        for (int row = 1; row < lines.Length; row++)
        {
            var cols = Split(lines[row]);
            if (cols.Length < 2) continue;
            if (!double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double twa)) continue;

            var speeds = new double[tws.Length];
            for (int c = 0; c < tws.Length && c + 1 < cols.Length; c++)
                _ = double.TryParse(cols[c + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out speeds[c]);

            twaList.Add(twa);
            speedRows.Add(speeds);
        }

        _tws = tws;
        _twa = [.. twaList];
        _speeds = new double[_twa.Length, _tws.Length];
        for (int r = 0; r < _twa.Length; r++)
            for (int c = 0; c < _tws.Length; c++)
                _speeds[r, c] = speedRows[r][c];
    }

    private static string[] Split(string line) => line.Split([',', ';', '\t']);
}
