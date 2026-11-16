using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Auth;

namespace OnaPlotter.Tests.Startup;

public class LoginStatusPollerTests
{
    internal sealed class FakeAuthApi : IAuthApi
    {
        public LoginStatus? Next { get; set; }
        public Exception? Throw { get; set; }
        public TaskCompletionSource<LoginStatus?>? Gate { get; set; }
        public int Calls;

        public Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null) return Task.FromException<LoginStatus?>(Throw);
            return Gate is not null ? Gate.Task : Task.FromResult(Next);
        }

        public Task<LoginResult?> LoginAsync(string username, string password, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static LoginStatus LoggedOut() => new() { AuthenticationRequired = true, Status = "notLoggedIn" };
    private static LoginStatus LoggedIn() => new() { AuthenticationRequired = true, Status = "loggedIn" };

    private static (LoginStatusPoller Sut, FakeAuthApi Api, FakeTimeProvider Time, List<int> Raised) Create()
    {
        var api = new FakeAuthApi();
        var time = new FakeTimeProvider();
        var sut = new LoginStatusPoller(api, time, NullLogger<LoginStatusPoller>.Instance);
        var raised = new List<int>();
        sut.OnChanged += () => raised.Add(raised.Count + 1);
        return (sut, api, time, raised);
    }

    [Test]
    public async Task Does_Not_Probe_Before_StartAsync()
    {
        var (_, api, time, _) = Create();

        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(api.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task StartAsync_Probes_Once_Immediately_And_Publishes_The_Status()
    {
        var (sut, api, _, raised) = Create();
        api.Next = LoggedOut();

        var start = sut.StartAsync(CancellationToken.None);

        await Assert.That(start.IsCompletedSuccessfully).IsTrue();
        await Assert.That(api.Calls).IsEqualTo(1);
        await Assert.That(sut.Current).IsSameReferenceAs(api.Next);
        await Assert.That(raised.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Polls_Every_Two_Minutes_While_The_Warning_Is_Hidden()
    {
        var (sut, api, time, _) = Create();
        api.Next = LoggedIn();
        await sut.StartAsync(CancellationToken.None);

        time.Advance(LoginStatusPoller.WarningHiddenInterval - TimeSpan.FromSeconds(1));
        await Assert.That(api.Calls).IsEqualTo(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(api.Calls).IsEqualTo(2);
        time.Advance(LoginStatusPoller.WarningHiddenInterval);
        await Assert.That(api.Calls).IsEqualTo(3);
    }

    [Test]
    public async Task Speeds_Up_To_Four_Seconds_While_The_Warning_Shows_And_Slows_Down_After()
    {
        var (sut, api, time, _) = Create();
        api.Next = LoggedOut();
        await sut.StartAsync(CancellationToken.None);
        await Assert.That(api.Calls).IsEqualTo(1);

        time.Advance(LoginStatusPoller.WarningShownInterval);
        time.Advance(LoginStatusPoller.WarningShownInterval);
        await Assert.That(api.Calls).IsEqualTo(3);

        api.Next = LoggedIn();
        time.Advance(LoginStatusPoller.WarningShownInterval);
        await Assert.That(api.Calls).IsEqualTo(4);

        // Now hidden: the fast cadence is gone.
        time.Advance(LoginStatusPoller.WarningShownInterval * 5);
        await Assert.That(api.Calls).IsEqualTo(4);
        time.Advance(LoginStatusPoller.WarningHiddenInterval);
        await Assert.That(api.Calls).IsEqualTo(5);
    }

    [Test]
    public async Task Raises_Only_When_The_Warning_State_Changes()
    {
        var (sut, api, time, raised) = Create();
        api.Next = LoggedIn();
        await sut.StartAsync(CancellationToken.None);
        await Assert.That(raised.Count).IsEqualTo(0);

        time.Advance(LoginStatusPoller.WarningHiddenInterval);
        await Assert.That(raised.Count).IsEqualTo(0);

        api.Next = LoggedOut();
        time.Advance(LoginStatusPoller.WarningHiddenInterval);
        await Assert.That(raised.Count).IsEqualTo(1);

        time.Advance(LoginStatusPoller.WarningShownInterval);
        await Assert.That(raised.Count).IsEqualTo(1);

        api.Next = LoggedIn();
        time.Advance(LoginStatusPoller.WarningShownInterval);
        await Assert.That(raised.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Throwing_Probe_Keeps_Status_Null_Result_Clears_It()
    {
        var (sut, api, time, raised) = Create();
        api.Next = LoggedOut();
        await sut.StartAsync(CancellationToken.None);
        await Assert.That(raised.Count).IsEqualTo(1);

        api.Throw = new HttpRequestException("offline");
        await sut.RefreshAsync();

        // A throwing probe leaves the previous state; a null result clears it.
        await Assert.That(sut.Current).IsNotNull();
        api.Throw = null;
        api.Next = null;
        time.Advance(LoginStatusPoller.WarningShownInterval);
        await Assert.That(sut.Current).IsNull();
        await Assert.That(raised.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Throwing_Subscriber_Does_Not_Prevent_The_Cadence_Switch()
    {
        var (sut, api, time, _) = Create();
        sut.OnChanged += () => throw new InvalidOperationException("subscriber");
        api.Next = LoggedOut();
        await sut.StartAsync(CancellationToken.None);
        await Assert.That(api.Calls).IsEqualTo(1);

        time.Advance(LoginStatusPoller.WarningShownInterval);

        await Assert.That(api.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Overlapping_Probes_Are_Coalesced()
    {
        var (sut, api, time, _) = Create();
        api.Gate = new TaskCompletionSource<LoginStatus?>();
        await sut.StartAsync(CancellationToken.None);
        await Assert.That(api.Calls).IsEqualTo(1);

        time.Advance(LoginStatusPoller.WarningHiddenInterval);
        _ = sut.RefreshAsync();
        await Assert.That(api.Calls).IsEqualTo(1);

        api.Gate.SetResult(LoggedIn());
        await sut.RefreshAsync();
        await Assert.That(api.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task RefreshAsync_Probes_On_Demand()
    {
        var (sut, api, _, _) = Create();
        api.Next = LoggedIn();

        await sut.RefreshAsync();

        await Assert.That(api.Calls).IsEqualTo(1);
        await Assert.That(sut.Current).IsSameReferenceAs(api.Next);
    }

    [Test]
    public async Task StopAsync_Stops_Polling()
    {
        var (sut, api, time, _) = Create();
        api.Next = LoggedOut();
        await sut.StartAsync(CancellationToken.None);

        await sut.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(api.Calls).IsEqualTo(1);
    }
}
