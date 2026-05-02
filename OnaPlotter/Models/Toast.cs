namespace OnaPlotter.Models;

/// <summary>
/// A transient notification surfaced by <c>IToastService</c>. Lives in
/// the Models namespace so consumers (MainLayout, page components,
/// alarm publishers) reference the type by its lifted name without
/// reaching into the service implementation. When
/// <see cref="ActionLabel"/> is non-null the UI renders an action
/// button; clicking it invokes <see cref="Action"/> and dismisses the
/// toast.
/// <para>
/// <see cref="IsPinned"/> = true marks a toast that the auto-dismiss
/// sweep ignores: only an explicit <c>Toasts.Dismiss</c> (or a tap on
/// the toast) clears it. Used for the MOB lat/lon read-back toast
/// where a helm reading the coords aloud must not lose the values
/// to a 30 s timer.
/// </para>
/// </summary>
public sealed record Toast(
    Guid Id,
    string Message,
    ToastLevel Level,
    DateTime ExpiresAt,
    string? ActionLabel = null,
    Func<Task>? Action = null,
    bool IsPinned = false);

/// <summary>Severity tier for a toast. Drives both the colour band on
/// the rendered card and the aria-live politeness (Error => assertive,
/// the rest => polite) for screen readers.</summary>
public enum ToastLevel { Info, Success, Warning, Error }
