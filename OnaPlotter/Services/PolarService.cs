// Boat polar performance data: target boat speed for each TWA/TWS combination.
// Standard polar format: CSV with TWA in first column, TWS values as headers.
// Example:
//   TWA,6,8,10,12,14,16,20
//   52,5.2,6.1,6.8,7.1,7.3,7.4,7.5
//   60,5.5,6.4,7.1,7.4,7.6,7.7,7.8
//   ...

using System.Globalization;
using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// Manages boat polar data. Interpolates target boat speed for given TWA and TWS.
/// Stored in localStorage for persistence.
/// </summary>
public sealed class PolarService
{
    private readonly IJSRuntime _js;
    private double[] _twsColumns = [];       // TWS values (knots) from header row
    private double[] _twaRows = [];          // TWA values (degrees) from first column
    private double[,] _speeds = new double[0, 0]; // [twa_index, tws_index] = target speed (knots)
    private bool _loaded;

    public bool HasPolar => _twaRows.Length > 0;
    public double[] TwsValues => _twsColumns;
    public double[] TwaValues => _twaRows;

    public event Action? OnPolarChanged;

    public PolarService(IJSRuntime js) => _js = js;

    /// <summary>
    /// Load polar from localStorage on startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var csv = await _js.InvokeAsync<string?>("localStorage.getItem", "ona.polar");
            if (!string.IsNullOrEmpty(csv))
                ParseCsv(csv);
        }
        catch { /* Ignore storage errors. */ }
    }

    /// <summary>
    /// Import a polar CSV string. Persists to localStorage.
    /// </summary>
    public async Task ImportAsync(string csv)
    {
        ParseCsv(csv);
        try { await _js.InvokeVoidAsync("localStorage.setItem", "ona.polar", csv); }
        catch { /* Ignore. */ }
        OnPolarChanged?.Invoke();
    }

    /// <summary>
    /// Clear the polar data.
    /// </summary>
    public async Task ClearAsync()
    {
        _twsColumns = [];
        _twaRows = [];
        _speeds = new double[0, 0];
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "ona.polar"); }
        catch { /* Ignore. */ }
        OnPolarChanged?.Invoke();
    }

    /// <summary>
    /// Interpolate target boat speed (knots) for given TWA (degrees, 0-180) and TWS (knots).
    /// Returns null if no polar data is loaded.
    /// </summary>
    public double? GetTargetSpeed(double twaDeg, double twsKn)
    {
        if (!HasPolar) return null;

        double absTwa = Math.Abs(twaDeg);
        if (absTwa > 180) absTwa = 360 - absTwa;

        // Find bracketing TWA indices.
        int twaLo = 0, twaHi = 0;
        for (int i = 0; i < _twaRows.Length - 1; i++)
        {
            if (_twaRows[i] <= absTwa && _twaRows[i + 1] >= absTwa) { twaLo = i; twaHi = i + 1; break; }
            if (i == _twaRows.Length - 2) { twaLo = twaHi = i + 1; }
        }

        // Find bracketing TWS indices.
        int twsLo = 0, twsHi = 0;
        for (int i = 0; i < _twsColumns.Length - 1; i++)
        {
            if (_twsColumns[i] <= twsKn && _twsColumns[i + 1] >= twsKn) { twsLo = i; twsHi = i + 1; break; }
            if (i == _twsColumns.Length - 2) { twsLo = twsHi = i + 1; }
        }

        // Clamp to edges.
        if (twsKn <= _twsColumns[0]) { twsLo = twsHi = 0; }
        if (twsKn >= _twsColumns[^1]) { twsLo = twsHi = _twsColumns.Length - 1; }
        if (absTwa <= _twaRows[0]) { twaLo = twaHi = 0; }
        if (absTwa >= _twaRows[^1]) { twaLo = twaHi = _twaRows.Length - 1; }

        // Bilinear interpolation.
        double twaFrac = (twaLo == twaHi) ? 0 : (absTwa - _twaRows[twaLo]) / (_twaRows[twaHi] - _twaRows[twaLo]);
        double twsFrac = (twsLo == twsHi) ? 0 : (twsKn - _twsColumns[twsLo]) / (_twsColumns[twsHi] - _twsColumns[twsLo]);

        double s00 = _speeds[twaLo, twsLo];
        double s01 = _speeds[twaLo, twsHi];
        double s10 = _speeds[twaHi, twsLo];
        double s11 = _speeds[twaHi, twsHi];

        double top = s00 + (s01 - s00) * twsFrac;
        double bot = s10 + (s11 - s10) * twsFrac;
        return top + (bot - top) * twaFrac;
    }

    /// <summary>
    /// Get the full polar curve for a given TWS (knots) as an array of (twaDeg, speedKn) pairs.
    /// Used for polar diagram overlay.
    /// </summary>
    public (double Twa, double Speed)[] GetPolarCurve(double twsKn)
    {
        if (!HasPolar) return [];
        var result = new (double, double)[_twaRows.Length];
        for (int i = 0; i < _twaRows.Length; i++)
        {
            result[i] = (_twaRows[i], GetTargetSpeed(_twaRows[i], twsKn) ?? 0);
        }
        return result;
    }

    /// <summary>
    /// Returns performance ratio (0-1+) of current speed vs polar target.
    /// >1 means exceeding polar. null if no polar data.
    /// </summary>
    public double? GetPerformance(double sogKn, double twaDeg, double twsKn)
    {
        var target = GetTargetSpeed(twaDeg, twsKn);
        if (target is null || target <= 0) return null;
        return sogKn / target.Value;
    }

    private void ParseCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return;

        // Header: TWA, tws1, tws2, ...
        var header = lines[0].Split(new[] { ',', ';', '\t' });
        if (header.Length < 2) return;

        _twsColumns = new double[header.Length - 1];
        for (int i = 1; i < header.Length; i++)
        {
            if (!double.TryParse(header[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
            _twsColumns[i - 1] = v;
        }

        var twaList = new List<double>();
        var speedRows = new List<double[]>();

        for (int row = 1; row < lines.Length; row++)
        {
            var cols = lines[row].Split(new[] { ',', ';', '\t' });
            if (cols.Length < 2) continue;
            if (!double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double twa)) continue;

            twaList.Add(twa);
            var speeds = new double[_twsColumns.Length];
            for (int c = 0; c < _twsColumns.Length && c + 1 < cols.Length; c++)
            {
                double.TryParse(cols[c + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out speeds[c]);
            }
            speedRows.Add(speeds);
        }

        _twaRows = [.. twaList];
        _speeds = new double[_twaRows.Length, _twsColumns.Length];
        for (int r = 0; r < _twaRows.Length; r++)
            for (int c = 0; c < _twsColumns.Length; c++)
                _speeds[r, c] = speedRows[r][c];
    }
}
