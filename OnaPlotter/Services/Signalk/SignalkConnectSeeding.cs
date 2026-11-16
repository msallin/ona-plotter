using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// REST round-trips that fill in what the delta stream can't replay on
/// connect. SignalK subscriptions only deliver changes, so state that
/// was already settled before the socket opened (vessel names, the
/// self URN, an active route, the design draft) is fetched once per
/// connect; vessel names are re-polled while any target is unnamed.
/// Every call is fire-and-forget and best-effort: a failure leaves the
/// value to arrive through live deltas.
/// </summary>
internal sealed class SignalkConnectSeeding
{
    /// <summary>
    /// How often to re-poll <c>/signalk/v1/api/vessels</c> for AIS
    /// identity while at least one tracked vessel is unnamed. AIS Type 5
    /// static is broadcast every 6 minutes; a 2 minute cadence pulls the
    /// name in within one static cycle once SK has it while staying cheap
    /// on the network. Self-throttles via
    /// <see cref="AisStore.HasUnnamedAisVessels"/> so a fully-named chart
    /// never hits the endpoint at all.
    /// </summary>
    private static readonly TimeSpan VesselNamesReseedInterval = TimeSpan.FromMinutes(2);

    private readonly AisStore _ais;
    private readonly ILogger _logger;
    private readonly SignalkDraftSeeder _draft;
    private readonly SignalkSelfContextSeeder _selfContext;
    private readonly SignalkVesselNamesSeeder _vesselNames;
    private readonly SignalkCourseSeeder _course;

    // The seeders are constructed here rather than registered in DI, so
    // they get null loggers; the receive loop logs the surrounding
    // lifecycle and each seeder's failures are best-effort by design.
    public SignalkConnectSeeding(HttpClient http, ISignalKBaseUrl baseUrl, NavigationData data,
        AisStore ais, Action<string?> setSelfContext, Action onDataChanged, ILogger logger)
    {
        _ais = ais;
        _logger = logger;
        _draft = new SignalkDraftSeeder(http, baseUrl, data,
            NullLogger<SignalkDraftSeeder>.Instance, onDataChanged);
        _selfContext = new SignalkSelfContextSeeder(http, baseUrl,
            NullLogger<SignalkSelfContextSeeder>.Instance, setSelfContext);
        _vesselNames = new SignalkVesselNamesSeeder(http, baseUrl, ais,
            NullLogger<SignalkVesselNamesSeeder>.Instance);
        _course = new SignalkCourseSeeder(http, baseUrl, data,
            NullLogger<SignalkCourseSeeder>.Instance, onDataChanged);
    }

    /// <summary>Starts every seed for a freshly opened socket. The tasks
    /// are not awaited: WASM is single-threaded, so <c>Task.Run</c> would
    /// only hide continuations, and a bare discard still surfaces an
    /// unhandled fault on <c>TaskScheduler.UnobservedTaskException</c>.
    /// <paramref name="ct"/> is the connection's token; the periodic
    /// name loop ends with it.</summary>
    public void StartOnConnect(CancellationToken ct)
    {
        // Static AIS identity received before we connected is never
        // replayed over the stream; without this vessels show a bare
        // MMSI on first paint.
        _ = _vesselNames.SeedAsync(ct);

        // Some servers update a vessel's name in their tree without
        // re-emitting a name delta; keep re-seeding while any vessel is
        // unnamed so a name that reaches SK later still reaches the chart.
        _ = PeriodicVesselNamesReseedAsync(ct);

        // Identify ourselves so own-boat is filtered out of the AIS list
        // even on servers that never send a hello with "self".
        _ = _selfContext.SeedAsync(ct);

        // A route activated before our socket opened (another plotter, a
        // previous session) never arrives on the delta stream.
        _ = _course.SeedAsync(ct);

        _ = _draft.SeedAsync(ct);
    }

    /// <summary>Runs the course seed on its own; exposed for tests that
    /// drive it with a mock HttpClient.</summary>
    internal Task SeedCourseAsync(CancellationToken ct) => _course.SeedAsync(ct);

    private async Task PeriodicVesselNamesReseedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(VesselNamesReseedInterval, ct);
                if (!_ais.HasUnnamedAisVessels()) continue;
                await _vesselNames.SeedAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Reconnect or dispose; a new loop starts on the next connect.
                return;
            }
            catch (Exception ex)
            {
                // A transient failure (network blip, REST 5xx) must not
                // end the re-seed for the rest of the session.
                _logger.LogWarning(ex,
                    "Periodic AIS-name re-seed failed; will retry on next interval");
            }
        }
    }
}
