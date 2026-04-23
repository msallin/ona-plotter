using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// Default <see cref="IConfirmationService"/>: falls back to the
/// browser's native <c>window.confirm</c>. Not because that's ideal
/// (styling, i18n, keyboard focus management are all worse than a
/// Blazor modal) but because it works on every helm screen + phone
/// + kiosk with zero CSS footprint. The whole point of the service
/// abstraction is that a future styled-modal implementation can be
/// swapped in without touching the 8+ callsites.
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    private readonly IJSRuntime _js;

    public ConfirmationService(IJSRuntime js) => _js = js;

    public async Task<bool> ConfirmAsync(string message, bool destructive = true)
    {
        try
        {
            return await _js.InvokeAsync<bool>("confirm", message);
        }
        catch (JSDisconnectedException)
        {
            // Page is tearing down (teardown race on navigate away).
            // Treat as "cancel" -- safer than proceeding with a
            // destructive action the user couldn't acknowledge.
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
