namespace OnaPlotter.Services.Api;

/// <summary>Autopilot state control via the SignalK Autopilot API.</summary>
public interface IAutopilotApi
{
    Task<ApiResult> SetStateAsync(string state, CancellationToken ct = default);
    Task<ApiResult> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default);
}
