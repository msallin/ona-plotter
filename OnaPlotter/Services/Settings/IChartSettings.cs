namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for chart-management consumers (Layers panel,
/// quick-bar, chart loader). Charts are large + ordered, so the
/// API uses sets / lists.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003.
/// </para>
/// </summary>
public interface IChartSettings
{
    /// <summary>True once first-run chart seeding has run.</summary>
    bool ChartsSeeded { get; }

    /// <summary>Chart IDs the user has enabled.</summary>
    IReadOnlySet<string> EnabledChartIds { get; }

    /// <summary>Route IDs the user has enabled.</summary>
    IReadOnlySet<string> EnabledRouteIds { get; }

    /// <summary>Charts in the on-map quick-switch bar (curated
    /// subset of <see cref="EnabledChartIds"/>).</summary>
    IReadOnlySet<string> QuickBarChartIds { get; }

    /// <summary>User-preferred chart draw order (first = bottom of
    /// stack, last = top). Charts not in this list render in
    /// server-provided order after any ordered charts.</summary>
    IReadOnlyList<string> ChartOrder { get; }

    Task MarkChartsSeededAsync();
    Task SetEnabledChartsAsync(IEnumerable<string> ids);
    Task SetEnabledRoutesAsync(IEnumerable<string> ids);
    Task SetQuickBarChartsAsync(IEnumerable<string> ids);
    Task SetChartOrderAsync(IEnumerable<string> ids);
}
