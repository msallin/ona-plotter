namespace OnaPlotter.Models;

/// <summary>A target that the user has silenced for a fixed window. The
/// alarm rules consult <see cref="OnaPlotter.Services.AlarmEvaluationContext.IsSnoozed"/>
/// (a predicate) while the UI reads <see cref="OnaPlotter.Services.IAlarmManager.SnoozedTargets"/>
/// to render a small chip with the remaining countdown so the user remembers
/// what is being muted.</summary>
public sealed record SnoozedTarget(
    string TargetKey,
    string Label,
    DateTime ExpiresAt);
