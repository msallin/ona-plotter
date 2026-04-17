namespace OnaPlotter.Services;

/// <summary>Transient notification messages shown to the user.</summary>
public interface IToastService
{
    IReadOnlyList<ToastService.Toast> Active { get; }
    event Action? OnChanged;

    void Show(string message, ToastService.ToastLevel level = ToastService.ToastLevel.Info, int durationSec = 4);
    void Success(string message);
    void Warning(string message);
    void Error(string message);
    void Dismiss(Guid id);
}
