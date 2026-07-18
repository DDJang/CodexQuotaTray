use codex_quota_tray::alerts::{AlertKind, AlertTracker, crossed_threshold, is_new_cycle};
use codex_quota_tray::persistence::NotificationSettings;
use codex_quota_tray::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};

#[test]
fn crossing_is_strict_and_multiple_levels_emit_only_most_urgent() {
    assert!(crossed_threshold(51, 50, 50));
    assert!(!crossed_threshold(50, 49, 50));
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings {
        remaining_50_percent: true,
        remaining_20_percent: true,
        remaining_10_percent: true,
    };
    assert!(
        tracker
            .observe(&quota(80, 1, true), &settings)
            .alerts
            .is_empty()
    );
    let observation = tracker.observe(&quota(9, 1, true), &settings);
    assert_eq!(observation.alerts.len(), 1);
    assert_eq!(observation.alerts[0].kind, AlertKind::Remaining10);
    assert!(
        tracker
            .observe(&quota(8, 1, true), &settings)
            .alerts
            .is_empty()
    );
}

#[test]
fn invalid_percentages_neither_baseline_nor_notify() {
    let mut tracker = AlertTracker::new();
    let settings = NotificationSettings::default();
    assert!(
        tracker
            .observe(&quota(200, 1, false), &settings)
            .alerts
            .is_empty()
    );
    assert!(tracker.snapshot().windows.is_empty());
    assert!(
        tracker
            .observe(&quota(19, 1, true), &settings)
            .alerts
            .is_empty()
    );
}

#[test]
fn enabling_threshold_is_non_retroactive_with_or_without_current_data() {
    let mut tracker = AlertTracker::new();
    assert!(tracker.enable_threshold(20, None));
    let settings = NotificationSettings::default();
    assert!(
        tracker
            .observe(&quota(15, 1, true), &settings)
            .alerts
            .is_empty()
    );

    let mut tracker = AlertTracker::new();
    tracker.observe(
        &quota(15, 1, true),
        &NotificationSettings {
            remaining_50_percent: false,
            remaining_20_percent: false,
            remaining_10_percent: false,
        },
    );
    tracker.enable_threshold(20, Some(&quota(15, 1, true)));
    assert!(
        tracker
            .observe(&quota(14, 1, true), &settings)
            .alerts
            .is_empty()
    );
}

#[test]
fn cycle_tolerance_uses_utc_reset_advance_and_safe_fallback() {
    let week = Some(10_080);
    assert!(!is_new_cycle(Some(1_000), Some(1_100), week, 10, 95));
    assert!(is_new_cycle(
        Some(1_000),
        Some(1_000 + 5_040 * 60),
        week,
        10,
        95
    ));
    assert!(is_new_cycle(None, None, week, 20, 80));
    assert!(!is_new_cycle(None, None, week, 40, 79));
}

#[test]
fn stable_id_is_sha256_and_raw_id_is_never_persisted() {
    let summary = QuotaSummary {
        windows: vec![window(80, 1, true, Some("private-limit-id"))],
        issues: vec![],
        reset_credits: ResetCreditsState::Unavailable,
        rate_limit_reached: false,
    };
    let mut tracker = AlertTracker::new();
    tracker.observe(&summary, &NotificationSettings::default());
    let key = &tracker.snapshot().windows[0].pseudonymous_key;
    assert!(key.starts_with("sha256:"));
    assert!(!key.contains("private-limit-id"));
    assert_eq!(key.split(':').nth(1).unwrap().len(), 64);
}

fn quota(remaining: i64, cycle: i64, valid: bool) -> QuotaSummary {
    QuotaSummary {
        windows: vec![window(remaining, cycle, valid, Some("stable"))],
        issues: vec![],
        reset_credits: ResetCreditsState::Unavailable,
        rate_limit_reached: false,
    }
}

fn window(remaining: i64, cycle: i64, valid: bool, id: Option<&str>) -> QuotaWindow {
    QuotaWindow {
        limit_id: id.map(str::to_owned),
        limit_name: None,
        source_slot: "primary",
        used_percent: 100 - remaining,
        remaining_percent: remaining,
        percentage_valid: valid,
        window_duration_mins: Some(10_080),
        resets_at: Some(cycle * 10_080 * 60),
    }
}
