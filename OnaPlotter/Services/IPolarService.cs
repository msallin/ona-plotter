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
}
