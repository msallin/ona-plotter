using System.Globalization;

namespace OnaPlotter.Services.Auth;

/// <summary>
/// Default <see cref="ITokenStore"/>. Token + expiry held in memory;
/// optional persistence to <see cref="IKeyValueStore"/> when the helm
/// has "Remember session" enabled.
/// </summary>
public sealed class TokenStore : ITokenStore
{
    /// <summary>Skew window: a token expiring within this window is
    /// reported as already-expired by <see cref="IsValid"/>. 30 s
    /// matches .NET's default JWT clock-skew tolerance and absorbs
    /// typical helm-tablet vs. server time drift.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    /// <summary>localStorage keys. <c>auth.token.v1</c> holds the raw
    /// JWT string; <c>auth.expiresAt.v1</c> holds the UTC ISO-8601
    /// expiry. Schema version baked in so a future format flip can
    /// land without a deserialise crash on an existing helm tablet.</summary>
    private const string KeyToken = "auth.token.v1";
    private const string KeyExpiresAt = "auth.expiresAt.v1";

    private readonly IKeyValueStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _loaded;

    public TokenStore(IKeyValueStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public string? Token { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }

    public bool IsValid
    {
        get
        {
            if (string.IsNullOrEmpty(Token)) return false;
            if (ExpiresAtUtc is null) return false;
            return ExpiresAtUtc.Value - _time.GetUtcNow().UtcDateTime > ExpirySkew;
        }
    }

    public event Action? OnTokenChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            string? rawToken;
            string? rawExpiry;
            try
            {
                rawToken = await _store.GetAsync(KeyToken, ct);
                rawExpiry = await _store.GetAsync(KeyExpiresAt, ct);
            }
            catch
            {
                // Storage unreadable (private browsing, quota): start
                // with an empty store. The helm gets prompted to sign
                // in next time they need an authed call.
                _loaded = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(rawToken) || string.IsNullOrWhiteSpace(rawExpiry))
            {
                _loaded = true;
                return;
            }

            if (!DateTime.TryParse(rawExpiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expiresAtUtc))
            {
                // Corrupt expiry: treat as "no token" + clean storage so
                // the bad value doesn't keep loading.
                await TryRemoveAsync(ct);
                _loaded = true;
                return;
            }

            // Drop already-expired tokens at load. Otherwise a tab
            // sitting closed past the JWT lifetime would resurrect a
            // token that's guaranteed to 401 on first use.
            if (expiresAtUtc - _time.GetUtcNow().UtcDateTime <= ExpirySkew)
            {
                await TryRemoveAsync(ct);
                _loaded = true;
                return;
            }

            Token = rawToken;
            ExpiresAtUtc = expiresAtUtc;
            _loaded = true;
            OnTokenChanged?.Invoke();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task SetAsync(string token, DateTime expiresAtUtc, bool persist,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("Token must be non-empty.", nameof(token));
        if (expiresAtUtc.Kind != DateTimeKind.Utc)
            expiresAtUtc = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc);

        bool changed = !string.Equals(Token, token, StringComparison.Ordinal)
                       || ExpiresAtUtc != expiresAtUtc;

        Token = token;
        ExpiresAtUtc = expiresAtUtc;
        _loaded = true;

        if (persist)
        {
            try
            {
                await _store.SetAsync(KeyToken, token, ct);
                // Round-trip via "O" (round-trip ISO-8601) so the
                // load-side TryParse with RoundtripKind sees the
                // exact same byte string we wrote. Without "O" the
                // default ToString format loses sub-second precision
                // and the parse falls back to local-kind, drifting
                // the expiry by the helm's tz offset on every
                // load/save cycle.
                await _store.SetAsync(KeyExpiresAt,
                    expiresAtUtc.ToString("O", CultureInfo.InvariantCulture), ct);
            }
            catch
            {
                // Persistence failure (private browsing): the
                // in-memory copy still wins, but the token won't
                // survive a reload. Acceptable degraded mode.
            }
        }
        else
        {
            // Caller said don't persist - but a previous session may
            // have persisted a token under "Remember session ON".
            // Wipe it so the in-memory and on-disk views agree.
            await TryRemoveAsync(ct);
        }

        if (changed) OnTokenChanged?.Invoke();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        bool wasSet = !string.IsNullOrEmpty(Token);
        Token = null;
        ExpiresAtUtc = null;
        _loaded = true;
        await TryRemoveAsync(ct);
        if (wasSet) OnTokenChanged?.Invoke();
    }

    private async Task TryRemoveAsync(CancellationToken ct)
    {
        try
        {
            await _store.RemoveAsync(KeyToken, ct);
            await _store.RemoveAsync(KeyExpiresAt, ct);
        }
        catch
        {
            // No-op on storage failure - we're already in the
            // recovery path.
        }
    }
}
