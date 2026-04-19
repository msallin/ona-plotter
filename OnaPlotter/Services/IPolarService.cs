namespace OnaPlotter.Services;

/// <summary>Boat polar performance table with target-speed lookup.</summary>
public interface IPolarService
{
    bool HasPolar { get; }
    double[] TwsValues { get; }
    double[] TwaValues { get; }

    event Action? OnPolarChanged;

    Task InitializeAsync();
    Task ImportAsync(string csv);
    Task ClearAsync();

    double? GetTargetSpeed(double twaDeg, double twsKn);
    double? GetPerformance(double sogKn, double twaDeg, double twsKn);
    (double Twa, double Speed)[] GetPolarCurve(double twsKn);

    /// <summary>
    /// Optimal TWA for max upwind VMG at the given TWS. VMG = BSP * cos(TWA).
    /// Searches the polar for the TWA in [0, 90] deg that maximises VMG.
    /// Returns null if the polar isn't loaded or every candidate has zero speed.
    /// </summary>
    OptimalPoint? GetOptimalUpwind(double twsKn);

    /// <summary>
    /// Optimal TWA for max downwind VMG at the given TWS. VMG magnitude
    /// is BSP * |cos(TWA)| on the downwind side (TWA in [90, 180]).
    /// Returns null if the polar isn't loaded or every candidate has
    /// zero speed.
    /// </summary>
    OptimalPoint? GetOptimalDownwind(double twsKn);
}

/// <summary>A single "best point" from the polar: absolute TWA,
/// boat speed through water, and the VMG magnitude at that point.</summary>
public readonly record struct OptimalPoint(double TwaDeg, double BspKn, double VmgKn);
