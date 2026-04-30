using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>Transient notification messages shown to the user.</summary>
public interface IToastService
{
    IReadOnlyList<Toast> Active { get; }
    event Action? OnChanged;

    void Show(string message, ToastLevel level = ToastLevel.Info, int durationSec = 4);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Info(string message);

    /// <summary>Shows a toast with an action button. Returns the toast id
    /// in case the caller wants to dismiss it early.</summary>
    Guid ShowAction(string message, string actionLabel, Func<Task> action,
        ToastLevel level = ToastLevel.Info, int durationSec = 6);

    void Dismiss(Guid id);
}
