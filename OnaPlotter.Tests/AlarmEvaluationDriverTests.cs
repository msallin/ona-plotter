using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="AlarmEvaluationDriver"/>: alarms are evaluated on data
/// changes AND on a 1 Hz timer, so time-based rules (DEADMAN) fire and
/// stale server alarms clear while no data arrives. The AIS snapshot is
/// only built when an evaluation is actually due.
/// </summary>
public class AlarmEvaluationDriverTests
{
    /// <summary>Counts calls and records which vessel provider the
    /// manager would have invoked.</summary>
    private sealed class RecordingManager : IAlarmManager
    {
        private readonly AlarmManager _inner;
        public int ProviderCalls { get; private set; }
        public RecordingManager(AlarmManager inner) => _inner = inner;
        public AlarmManager Inner => _inner;

        public void Evaluate(NavigationData data, Func<IReadOnlyCollection<AisVessel>> vessels)
            => _inner.Evaluate(data, () => { ProviderCalls++; return vessels(); });
        public void Evaluate(NavigationData data, IReadOnlyCollection<AisVessel> vessels)
            => _inner.Evaluate(data, vessels);

        public AlarmInfo? ActiveAlarm => _inner.ActiveAlarm;
        public IReadOnlyList<AlarmInfo> ActiveAlarms => _inner.ActiveAlarms;
        public IReadOnlyList<AlarmInfo> AllActiveAlarms => _inner.AllActiveAlarms;
        public IReadOnlyList<(AlarmInfo Info, IAlarmRule Rule)> AllActiveEntries => _inner.AllActiveEntries;
        public int HiddenAlarmsCount => _inner.HiddenAlarmsCount;
        public IReadOnlyList<SnoozedTarget> SnoozedTargets => _inner.SnoozedTargets;
        public IReadOnlyList<AlarmRearmInfo> RearmStatuses(DateTime now) => _inner.RearmStatuses(now);
        public IReadOnlyList<DismissedAlarm> DismissedHistory => _inner.DismissedHistory;
        public event Action<AlarmInfo?>? OnAlarmChanged { add => _inner.OnAlarmChanged += value; remove => _inner.OnAlarmChanged -= value; }
        public event Action? OnAlarmsChanged { add => _inner.OnAlarmsChanged += value; remove => _inner.OnAlarmsChanged -= value; }
        public Task DismissAsync() => _inner.DismissAsync();
        public Task DismissAsync(AlarmInfo alarm) => _inner.DismissAsync(alarm);
        public Task SnoozeActiveAsync() => _inner.SnoozeActiveAsync();
        public Task SnoozeAsync(AlarmInfo alarm) => _inner.SnoozeAsync(alarm);
        public Task UnsnoozeAsync(string targetKey) => _inner.UnsnoozeAsync(targetKey);
        public int SnoozeDurationMinutes => _inner.SnoozeDurationMinutes;
        public IReadOnlyList<AlarmRuleId> RegisteredRuleIds => _inner.RegisteredRuleIds;
        public Task InitializeAsync() => _inner.InitializeAsync();
    }

    private sealed record Fixture(
        AlarmEvaluationDriver Driver, RecordingManager Manager, FakeTimeProvider Time,
        ServerNotificationStore Notifications, DeadmanTracker Deadman, FakeSettings Settings) : IDisposable
    {
        public void Dispose() => Driver.Dispose();
    }

    private static Fixture NewFixture()
    {
        // DeadmanTracker stamps interactions with the system clock, so
        // the fake clock starts at that stamp.
        var deadman = new DeadmanTracker(TimeProvider.System);
        var time = new FakeTimeProvider(new DateTimeOffset(deadman.LastInteractionUtc));
        var settings = new FakeSettings();
        var notifications = new ServerNotificationStore();
        var ais = new AisStore();
        IAlarmRule[] rules =
        [
            new DeadmanAlarmRule(deadman),
            new ServerNotificationsAlarmRule(notifications, NoopNotificationsApi.Instance, NoopPublishedAlarmTracker.Instance),
        ];
        var manager = new RecordingManager(AlarmManagerFactory.Create(rules, time, settings));
        var signalk = new SignalkClient(
            baseUrl: new OnaPlotter.Tests.Services.Resources.FakeBaseUrl(),
            loggerFactory: NullLoggerFactory.Instance,
            data: new OnaPlotter.Models.NavigationData(),
            track: new TrackBuffer(),
            ais: ais,
            http: new HttpClient(),
            settings: settings,
            serverNotifs: notifications,
            atons: new AtonStore(),
            time: time);
        var driver = new AlarmEvaluationDriver(manager, signalk, ais, notifications, time,
            NullLogger<AlarmEvaluationDriver>.Instance);
        return new Fixture(driver, manager, time, notifications, deadman, settings);
    }

    [Test]
    public async Task Timer_FiresDeadman_WithNoDataArriving()
    {
        // Disconnected boat: no deltas at all. DEADMAN is purely
        // time-based and must still fire from the timer tick.
        using var f = NewFixture();
        f.Settings.DeadmanTimeoutMinutes = 1;

        f.Time.Advance(TimeSpan.FromSeconds(61));

        await Assert.That(f.Manager.ActiveAlarm?.RuleId).IsEqualTo(AlarmRuleId.Deadman);
    }

    [Test]
    public async Task Timer_ClearsServerAlarmDroppedWithoutEvent()
    {
        // ServerNotificationStore.Reset (WS disconnect) raises no event.
        // The banner must still clear on the next timer tick.
        using var f = NewFixture();
        f.Notifications.Apply("notifications.mob", "emergency", "MOB");
        await Assert.That(f.Manager.AllActiveAlarms.Count).IsEqualTo(1);

        f.Notifications.Reset();
        f.Time.Advance(AlarmEvaluationDriver.TickInterval);

        await Assert.That(f.Manager.AllActiveAlarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotificationChange_EvaluatesImmediately()
    {
        using var f = NewFixture();
        f.Notifications.Apply("notifications.mob", "emergency", "MOB");
        await Assert.That(f.Manager.ActiveAlarm?.Title).IsEqualTo("MOB");
    }

    [Test]
    public async Task ThrottledCalls_DoNotBuildAisSnapshot()
    {
        // A burst of data changes inside one evaluation interval only
        // pulls the vessel snapshot once.
        using var f = NewFixture();
        for (int i = 0; i < 30; i++) f.Driver.Evaluate();
        await Assert.That(f.Manager.ProviderCalls).IsEqualTo(1);

        f.Time.Advance(AlarmEvaluationDriver.TickInterval);
        await Assert.That(f.Manager.ProviderCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Dispose_StopsTimerAndUnsubscribes()
    {
        var f = NewFixture();
        f.Driver.Evaluate();
        f.Dispose();
        int before = f.Manager.ProviderCalls;

        f.Time.Advance(TimeSpan.FromSeconds(5));
        f.Notifications.Apply("notifications.mob", "emergency", "MOB");

        await Assert.That(f.Manager.ProviderCalls).IsEqualTo(before);
    }
}
