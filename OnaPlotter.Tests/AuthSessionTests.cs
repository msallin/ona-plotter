using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Auth;

namespace OnaPlotter.Tests;

/// <summary>
/// AuthSession is the orchestration layer between IAuthApi (the wire),
/// ITokenStore (JWT lifecycle), and IServerSettings (credential
/// persistence flags). These tests pin the persistence rules - which
/// settings get written, which don't, and how the auto-login startup
/// path behaves under each combination.
/// </summary>
public class AuthSessionTests
{
    [Test]
    public async Task LoginAsync_Stores_Token_And_Username_Always()
    {
        var (session, settings, store, api) = Build();
        api.NextLogin = new LoginResult { Token = "jwt", TimeToLive = 3600 };

        var ok = await session.LoginAsync("ona", "pwd", rememberPassword: false);

        await Assert.That(ok).IsTrue();
        await Assert.That(store.Token).IsEqualTo("jwt");
        await Assert.That(settings.StoredUsername).IsEqualTo("ona");
        // rememberPassword=false: password NOT persisted, RememberPassword
        // explicitly cleared (so a previous opt-in doesn't leak forward).
        await Assert.That(settings.StoredPassword).IsEqualTo("");
        await Assert.That(settings.RememberPassword).IsFalse();
    }

    [Test]
    public async Task LoginAsync_With_RememberPassword_Persists_Both()
    {
        var (session, settings, store, api) = Build();
        api.NextLogin = new LoginResult { Token = "jwt", TimeToLive = 3600 };

        await session.LoginAsync("ona", "pwd", rememberPassword: true);

        await Assert.That(settings.RememberPassword).IsTrue();
        await Assert.That(settings.StoredPassword).IsEqualTo("pwd");
    }

    [Test]
    public async Task LoginAsync_Returns_False_When_Server_Rejects()
    {
        var (session, settings, store, api) = Build();
        api.NextLogin = null; // simulate 401 / network failure

        var ok = await session.LoginAsync("ona", "wrong", rememberPassword: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(store.Token).IsNull();
        // Settings UNCHANGED on failure - we don't want a typo-and-fail
        // attempt to persist a bad password.
        await Assert.That(settings.StoredUsername).IsEqualTo("");
        await Assert.That(settings.StoredPassword).IsEqualTo("");
    }

    [Test]
    public async Task LoginAsync_Persists_Token_When_RememberSession_True()
    {
        // RememberSession is the JWT-persistence gate. ON (default) means
        // the token store writes to localStorage. AuthSession just
        // forwards the flag; the assertion here is that the wiring
        // through to TokenStore.SetAsync(persist:) is right.
        var (session, settings, store, api) = Build();
        settings.RememberSession = true;
        api.NextLogin = new LoginResult { Token = "jwt-keep", TimeToLive = 3600 };

        await session.LoginAsync("ona", "pwd", rememberPassword: false);

        await Assert.That(store.LastSetPersist).IsTrue();
    }

    [Test]
    public async Task LoginAsync_Memory_Only_When_RememberSession_False()
    {
        var (session, settings, store, api) = Build();
        settings.RememberSession = false;
        api.NextLogin = new LoginResult { Token = "jwt-tab", TimeToLive = 3600 };

        await session.LoginAsync("ona", "pwd", rememberPassword: false);

        await Assert.That(store.LastSetPersist).IsFalse();
    }

    [Test]
    public async Task TryAutoLoginAsync_Returns_True_When_Existing_Token_Valid()
    {
        var (session, settings, store, api) = Build();
        store.SeedToken("existing-jwt", validForHours: 6);

        var ok = await session.TryAutoLoginAsync();

        await Assert.That(ok).IsTrue();
        // Token unchanged - no API call needed.
        await Assert.That(store.Token).IsEqualTo("existing-jwt");
        await Assert.That(api.LoginCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryAutoLoginAsync_Returns_False_When_No_Stored_Password()
    {
        var (session, settings, store, api) = Build();
        // Token expired + helm hasn't opted into password persistence:
        // there's nothing we can do silently.
        settings.RememberPassword = false;
        settings.StoredUsername = "ona";

        var ok = await session.TryAutoLoginAsync();

        await Assert.That(ok).IsFalse();
        await Assert.That(api.LoginCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryAutoLoginAsync_Reissues_Token_When_Password_Stored()
    {
        var (session, settings, store, api) = Build();
        settings.RememberPassword = true;
        settings.StoredUsername = "ona";
        settings.StoredPassword = "pwd";
        api.NextLogin = new LoginResult { Token = "jwt-fresh", TimeToLive = 3600 };

        var ok = await session.TryAutoLoginAsync();

        await Assert.That(ok).IsTrue();
        await Assert.That(store.Token).IsEqualTo("jwt-fresh");
        await Assert.That(api.LoginCallCount).IsEqualTo(1);
        await Assert.That(api.LastUsername).IsEqualTo("ona");
        await Assert.That(api.LastPassword).IsEqualTo("pwd");
    }

    [Test]
    public async Task LogoutAsync_Clears_Token_And_Password_But_Not_Username()
    {
        var (session, settings, store, api) = Build();
        store.SeedToken("jwt", validForHours: 1);
        settings.StoredUsername = "ona";
        settings.RememberPassword = true;
        settings.StoredPassword = "pwd";

        await session.LogoutAsync();

        await Assert.That(store.Token).IsNull();
        await Assert.That(settings.StoredPassword).IsEqualTo("");
        // Username stays - it's not a credential. Helm doesn't have to
        // re-type it on next sign-in.
        await Assert.That(settings.StoredUsername).IsEqualTo("ona");
        // Best-effort POST to /signalk/v1/auth/logout was attempted.
        await Assert.That(api.LogoutCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task LoginAsync_Floors_Tiny_TimeToLive()
    {
        // Some signalk-server variants return 0 / negative TTL on a
        // misconfigured token. AuthSession floors at 60 s so the
        // freshly-issued token can at least cover one in-flight
        // request rather than landing already-expired.
        var (session, settings, store, api) = Build();
        api.NextLogin = new LoginResult { Token = "jwt-tiny", TimeToLive = 0 };

        await session.LoginAsync("ona", "pwd", rememberPassword: false);

        await Assert.That(store.Token).IsEqualTo("jwt-tiny");
        await Assert.That(store.IsValid).IsTrue();
    }

    private static (AuthSession session, FakeSettings settings, RecordingTokenStore store, FakeAuthApi api) Build()
    {
        var settings = new FakeSettings();
        var time = new FakeTimeProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var store = new RecordingTokenStore(time);
        var api = new FakeAuthApi();
        var session = new AuthSession(api, store, settings, time, NullLogger<AuthSession>.Instance);
        return (session, settings, store, api);
    }

    private sealed class FakeAuthApi : IAuthApi
    {
        public LoginResult? NextLogin { get; set; }
        public int LoginCallCount { get; private set; }
        public int LogoutCallCount { get; private set; }
        public string? LastUsername { get; private set; }
        public string? LastPassword { get; private set; }

        public Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default)
            => Task.FromResult<LoginStatus?>(null);

        public Task<LoginResult?> LoginAsync(string username, string password, CancellationToken ct = default)
        {
            LoginCallCount++;
            LastUsername = username;
            LastPassword = password;
            return Task.FromResult(NextLogin);
        }

        public Task LogoutAsync(CancellationToken ct = default)
        {
            LogoutCallCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Test-double for ITokenStore that exposes the
    /// persist flag from the last SetAsync so tests can assert on it.</summary>
    private sealed class RecordingTokenStore : ITokenStore
    {
        private readonly TimeProvider _time;
        public RecordingTokenStore(TimeProvider time) { _time = time; }

        public string? Token { get; private set; }
        public DateTime? ExpiresAtUtc { get; private set; }
        public bool IsValid =>
            !string.IsNullOrEmpty(Token)
            && ExpiresAtUtc is not null
            && ExpiresAtUtc.Value - _time.GetUtcNow().UtcDateTime > TimeSpan.FromSeconds(30);
        public bool LastSetPersist { get; private set; }
        public event Action? OnTokenChanged;

        public void SeedToken(string token, double validForHours)
        {
            Token = token;
            ExpiresAtUtc = _time.GetUtcNow().UtcDateTime.AddHours(validForHours);
        }

        public Task SetAsync(string token, DateTime expiresAtUtc, bool persist, CancellationToken ct = default)
        {
            Token = token;
            ExpiresAtUtc = expiresAtUtc;
            LastSetPersist = persist;
            OnTokenChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            bool was = Token is not null;
            Token = null;
            ExpiresAtUtc = null;
            if (was) OnTokenChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
