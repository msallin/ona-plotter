namespace OnaPlotter.Services.Startup;

/// <summary>Silent login at startup (implemented by <c>AuthSession</c>).</summary>
public interface IAutoLogin
{
    Task<bool> TryAutoLoginAsync(CancellationToken ct = default);
}

/// <summary>Starts the SignalK WebSocket receive loop (implemented by
/// <c>SignalkClient</c>).</summary>
public interface ISocketStarter
{
    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Full REST reconcile of SignalK resources (implemented by
/// <c>ResourceStore</c>).</summary>
public interface IResourceRefresher
{
    Task RefreshAllAsync(string cause = "manual", CancellationToken ct = default);
}
