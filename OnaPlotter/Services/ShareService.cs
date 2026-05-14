using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// Routes a text payload through the platform's share or clipboard
/// surface and surfaces a toast for the helm.
///
/// <para>The chartplotter has several callers that share the same
/// "user tapped Share in a popup" path: waypoints, notes, MOB
/// markers (GeoJSON Feature payloads) and trip summaries (plain-
/// text recap). Each one builds a slightly different string but the
/// post-build flow is identical: import <c>fileTransfer.js</c>,
/// call <c>shareOrCopy</c>, branch on the four outcome strings, log
/// on failure. Centralising it here keeps the popup-side JSInvokables
/// thin and lets a follow-on (e.g. anchor-position share, route-
/// point share) drop in without copying the same try/catch + toast
/// switch a fifth time.</para>
/// </summary>
public sealed class ShareService
{
    private readonly IJSRuntime _js;
    private readonly IToastService _toasts;

    public ShareService(IJSRuntime js, IToastService toasts)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
    }

    /// <summary>Hand <paramref name="text"/> to the platform's
    /// share-sheet (iPad / Android Chrome) or copy it to the
    /// clipboard (desktop). Toasts the outcome.</summary>
    /// <param name="title">Title shown in the system share sheet
    /// (e.g. waypoint name, "MOB", "Note", trip name).</param>
    /// <param name="text">Payload to share - a pre-serialised
    /// GeoJSON Feature for resource shares, a human-readable
    /// multi-line summary for trip shares.</param>
    /// <param name="errorContext">Label used when an unexpected
    /// JS exception leaks; lets log readers separate
    /// MOB-share failures from waypoint-share failures.</param>
    public async Task ShareTextAsync(string title, string text, string errorContext = "Share")
    {
        try
        {
            var fileTransfer = await _js.InvokeAsync<IJSObjectReference>(
                "import", "./js/platform/fileTransfer.js");
            string outcome = await fileTransfer.InvokeAsync<string>("shareOrCopy", title, text);
            switch (outcome)
            {
                case "shared":    _toasts.Success("Shared"); break;
                case "copied":    _toasts.Success("Copied to clipboard"); break;
                case "cancelled": /* helm tapped cancel - silent */ break;
                default:          _toasts.Error("Couldn't share or copy."); break;
            }
        }
        catch (JSDisconnectedException) { /* page navigating; silent */ }
        catch (JSException ex) { _toasts.LogException(ex, errorContext); }
    }
}
