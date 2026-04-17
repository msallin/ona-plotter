namespace OnaPlotter.Services.Api;

/// <summary>Autopilot state control via the SignalK Autopilot API.</summary>
public interface IAutopilotApi
{
    Task<bool> SetStateAsync(string state, CancellationToken ct = default);
    Task<bool> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default);
}
