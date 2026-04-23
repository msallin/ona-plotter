namespace OnaPlotter.Services;

/// <summary>
/// Shared confirmation prompt for destructive actions (delete
/// waypoint / stop navigation / clear route / raise anchor / ...).
/// Every call site used to JSRuntime.InvokeAsync&lt;bool&gt;("confirm", msg)
/// independently, which meant any future swap to a styled modal
/// would have to touch 8+ files. Threading through a service lets
/// that swap live behind one implementation.
/// </summary>
public interface IConfirmationService
{
    /// <summary>
    /// Shows a prompt and resolves to <c>true</c> when the user
    /// confirms, <c>false</c> when they cancel or the prompt can't
    /// render (JSDisconnected on teardown).
    /// </summary>
    /// <param name="message">
    /// Human-readable question. The default implementation passes
    /// it verbatim to <c>window.confirm</c>; a styled-modal
    /// implementation would render it as the body text.
    /// </param>
    /// <param name="destructive">
    /// True for actions that delete data or cancel an in-progress
    /// operation. Default true because almost every callsite is
    /// a destructive action (delete, stop nav, raise anchor,
    /// discard unsaved route edits). A styled modal could paint
    /// the confirm button in the danger palette when set; the
    /// native <c>confirm</c> fallback ignores this parameter.
    /// </param>
    Task<bool> ConfirmAsync(string message, bool destructive = true);
}
