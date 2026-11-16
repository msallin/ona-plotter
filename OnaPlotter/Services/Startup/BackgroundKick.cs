namespace OnaPlotter.Services.Startup;

/// <summary>
/// Starts work that must not delay first render (network calls, retry
/// loops) and makes sure a failure is observed instead of vanishing
/// into an unobserved task exception.
/// </summary>
internal static class BackgroundKick
{
    /// <summary>
    /// Invokes <paramref name="work"/> synchronously up to its first
    /// await, so the call order between kicks is deterministic, then
    /// lets the remainder run in the background. A fault is logged as a
    /// warning; it never reaches the caller.
    /// </summary>
    public static void Run(Func<Task> work, ILogger logger, string description)
    {
        _ = RunAsync(work, logger, description);
    }

    private static async Task RunAsync(Func<Task> work, ILogger logger, string description)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // No token is passed to kicks, so this is the shared
            // HttpClient timeout (or a disposed dependency).
            logger.LogWarning(ex, "Startup task '{Task}' timed out or was cancelled", description);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup task '{Task}' failed", description);
        }
    }
}
