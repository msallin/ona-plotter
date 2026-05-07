namespace OnaPlotter.Models;

/// <summary>
/// Numeric values the SignalK Radar API v3.1 accepts for the
/// <c>power</c> control. Spec defines four states (0 Off, 1 Standby,
/// 2 Transmit, 3 Preparing) but only Standby and Transmit are settable
/// by the operator - Off / Preparing are radar-internal transitions.
/// </summary>
public enum RadarPower
{
    Off = 0,
    Standby = 1,
    Transmit = 2,
    Preparing = 3,
}
