using OnaPlotter.Services.Api;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pinning the contract that the JS-invokable Deactivate path on the
/// active-route popup -- and the Stop Navigation context-menu entry --
/// share via Map.razor.StopNavigation. The pure helper is the testable
/// surface; Map.razor's job is to drive the visual side-effects.
///
/// Acts on review finding TEST-003 (DeactivateActiveRoute had no C#
/// test; the production "tap Deactivate -> server clears the course"
/// path was unverified).
/// </summary>
public class StopNavigationFlowTests
{
    /// <summary>Fake CourseApi that records whether ClearAsync was
    /// called and lets the test set the result. Other ICourseApi
    /// methods throw -- the helper only uses Clear.</summary>
    private sealed class FakeCourseApi : ICourseApi
    {
        public int ClearCalls { get; private set; }
        public ApiResult ClearResult { get; set; } = ApiResult.Ok;
        public Exception? ClearThrows { get; set; }

        public Task<ApiResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCalls++;
            if (ClearThrows is not null) throw ClearThrows;
            return Task.FromResult(ClearResult);
        }

        public Task<ApiResult> SetDestinationAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApiResult> SetDestinationPositionAsync(double lat, double lon, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApiResult> SetActiveRouteAsync(string routeId, int p = 0, bool r = false, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApiResult> AdvanceActiveRouteAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApiResult> SetPointIndexAsync(int p, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Test]
    public async Task RequestStop_NoPending_FiresClearAsync_ReturnsStopped()
    {
        // Happy path: no previous Stop in flight, server accepts the
        // clear. The helper reports Stopped so the caller stamps the
        // pending timestamp + dims the route HUD.
        var api = new FakeCourseApi();

        var result = await StopNavigationFlow.RequestStopAsync(api, pendingSince: null);

        await Assert.That(result.Outcome).IsEqualTo(StopNavigationOutcome.Stopped);
        await Assert.That(result.ErrorMessage).IsNull();
        await Assert.That(api.ClearCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RequestStop_WhenPendingSet_IsNoOp()
    {
        // A second tap during the in-flight window must not fire a
        // duplicate ClearAsync (idempotent on the server, but adds a
        // misleading toast + a duplicate audit-log entry SK-side).
        var api = new FakeCourseApi();

        var result = await StopNavigationFlow.RequestStopAsync(
            api, pendingSince: DateTime.UtcNow.AddSeconds(-2));

        await Assert.That(result.Outcome).IsEqualTo(StopNavigationOutcome.AlreadyPending);
        await Assert.That(api.ClearCalls).IsEqualTo(0);
    }

    [Test]
    public async Task RequestStop_ApiReturnsFailure_ReportsFailedWithError()
    {
        // Server rejects (PUT 4xx, plugin offline, network glitch):
        // the helper preserves the server's error message so the
        // caller can toast it. C# state stays clean; next tap retries.
        var api = new FakeCourseApi { ClearResult = ApiResult.Fail("server offline") };

        var result = await StopNavigationFlow.RequestStopAsync(api, pendingSince: null);

        await Assert.That(result.Outcome).IsEqualTo(StopNavigationOutcome.Failed);
        await Assert.That(result.ErrorMessage).IsEqualTo("server offline");
    }

    [Test]
    public async Task RequestStop_ApiThrows_ReportsFailedWithExceptionMessage()
    {
        // HTTP layer threw (DNS, refused, timeout). Same Failed
        // outcome, message preserved for the toast.
        var api = new FakeCourseApi { ClearThrows = new HttpRequestException("connection refused") };

        var result = await StopNavigationFlow.RequestStopAsync(api, pendingSince: null);

        await Assert.That(result.Outcome).IsEqualTo(StopNavigationOutcome.Failed);
        await Assert.That(result.ErrorMessage).IsEqualTo("connection refused");
        await Assert.That(api.ClearCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RequestStop_NullApi_Throws()
    {
        // Defensive: a misconfigured DI container handing in null
        // surfaces as a clear ArgumentNullException at the call site
        // rather than a NullReferenceException three frames deeper.
        await Assert.That(async () =>
            await StopNavigationFlow.RequestStopAsync(null!, pendingSince: null))
            .Throws<ArgumentNullException>();
    }
}
