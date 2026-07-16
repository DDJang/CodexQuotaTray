use codex_quota_tray::alerts::{AlertKind, AlertTracker};
use codex_quota_tray::persistence::NotificationSettings;
use codex_quota_tray::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};

#[test]
fn thresholds_fire_once_when_crossed_and_not_on_initial_observation() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    assert!(tracker.observe(&quota(79, 1), &settings).is_empty());

    assert_eq!(
        kinds(tracker.observe(&quota(80, 1), &settings)),
        [AlertKind::Remaining20]
    );
    assert!(tracker.observe(&quota(81, 1), &settings).is_empty());
    assert_eq!(
        kinds(tracker.observe(&quota(95, 1), &settings)),
        [AlertKind::Remaining5]
    );
    assert!(tracker.observe(&quota(96, 1), &settings).is_empty());
    assert_eq!(
        kinds(tracker.observe(&quota(100, 1), &settings)),
        [AlertKind::Exhausted]
    );
    assert!(tracker.observe(&quota(100, 1), &settings).is_empty());
}

#[test]
fn a_large_drop_emits_only_the_most_severe_alert() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    tracker.observe(&quota(70, 1), &settings);
    assert_eq!(
        kinds(tracker.observe(&quota(100, 1), &settings)),
        [AlertKind::Exhausted]
    );
}

#[test]
fn a_new_cycle_can_emit_recovery_and_rearms_thresholds() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    tracker.observe(&quota(79, 1), &settings);
    tracker.observe(&quota(80, 1), &settings);
    assert_eq!(
        kinds(tracker.observe(&quota(10, 2), &settings)),
        [AlertKind::Recovered]
    );
    assert_eq!(
        kinds(tracker.observe(&quota(80, 2), &settings)),
        [AlertKind::Remaining20]
    );
}

#[test]
fn disabled_threshold_is_suppressed_without_replaying_later() {
    let mut tracker = AlertTracker::new();
    let mut settings = NotificationSettings {
        remaining_20_percent: false,
        ..NotificationSettings::default()
    };
    tracker.observe(&quota(79, 1), &settings);
    assert!(tracker.observe(&quota(80, 1), &settings).is_empty());
    settings.remaining_20_percent = true;
    assert!(tracker.observe(&quota(81, 1), &settings).is_empty());
}

#[test]
fn windows_are_tracked_independently() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    tracker.observe(&two_windows(79, 50), &settings);
    let alerts = tracker.observe(&two_windows(80, 95), &settings);
    assert_eq!(alerts.len(), 2);
    assert!(
        alerts
            .iter()
            .any(|alert| alert.kind == AlertKind::Remaining20)
    );
    assert!(
        alerts
            .iter()
            .any(|alert| alert.kind == AlertKind::Remaining5)
    );
}

#[test]
fn aggregate_server_limit_signal_is_silent_on_baseline_and_deduplicated() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    let mut snapshot = quota(79, 1);
    assert!(tracker.observe(&snapshot, &settings).is_empty());

    snapshot = quota(80, 1);
    snapshot.rate_limit_reached = true;
    assert_eq!(
        kinds(tracker.observe(&snapshot, &settings)),
        [AlertKind::Exhausted]
    );
    assert!(tracker.observe(&snapshot, &settings).is_empty());

    snapshot.rate_limit_reached = false;
    assert_eq!(
        kinds(tracker.observe(&snapshot, &settings)),
        [AlertKind::Recovered]
    );
}

fn kinds(alerts: Vec<codex_quota_tray::alerts::QuotaAlert>) -> Vec<AlertKind> {
    alerts.into_iter().map(|alert| alert.kind).collect()
}

fn quota(used_percent: i64, cycle: i64) -> QuotaSummary {
    QuotaSummary {
        windows: vec![window("primary", 300, used_percent, cycle)],
        issues: Vec::new(),
        reset_credits: ResetCreditsState::UnavailableInSchema,
        rate_limit_reached: false,
    }
}

fn two_windows(first_used: i64, second_used: i64) -> QuotaSummary {
    QuotaSummary {
        windows: vec![
            window("primary", 300, first_used, 1),
            window("secondary", 10_080, second_used, 1),
        ],
        issues: Vec::new(),
        reset_credits: ResetCreditsState::UnavailableInSchema,
        rate_limit_reached: false,
    }
}

fn window(source_slot: &'static str, duration: i64, used_percent: i64, cycle: i64) -> QuotaWindow {
    QuotaWindow {
        limit_id: None,
        limit_name: None,
        source_slot,
        used_percent,
        remaining_percent: 100 - used_percent,
        window_duration_mins: Some(duration),
        resets_at: Some(1_800_000_000 + cycle),
    }
}
