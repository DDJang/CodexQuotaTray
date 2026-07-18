use codex_quota_tray::refresh::{
    CompletionOutcome, CoordinatorAction, RefreshCoordinator, RefreshMode, RefreshPolicy,
    RefreshReason, RequestDecision, base_interval_secs, failure_backoff_secs, reason_allowed,
    stale_after_secs,
};

#[test]
fn modes_and_dynamic_stale_thresholds_are_deterministic() {
    assert_eq!(base_interval_secs(RefreshMode::Auto, None), Some(300));
    assert_eq!(base_interval_secs(RefreshMode::Auto, Some(51)), Some(1_800));
    assert_eq!(base_interval_secs(RefreshMode::Auto, Some(21)), Some(900));
    assert_eq!(base_interval_secs(RefreshMode::Auto, Some(20)), Some(300));
    assert_eq!(stale_after_secs(RefreshMode::Auto, Some(51), 0), 3_600);
    assert_eq!(stale_after_secs(RefreshMode::ManualOnly, Some(1), 8), 3_600);
    assert_eq!([1, 2, 3, 4].map(failure_backoff_secs), [60, 120, 300, 900]);
}

#[test]
fn manual_only_suppresses_every_reason_except_manual() {
    for reason in [
        RefreshReason::Startup,
        RefreshReason::RateLimitNotification,
        RefreshReason::Resume,
        RefreshReason::NetworkRestored,
        RefreshReason::CardOpened,
        RefreshReason::Scheduled,
    ] {
        assert!(!reason_allowed(RefreshMode::ManualOnly, reason));
    }
    assert!(reason_allowed(
        RefreshMode::ManualOnly,
        RefreshReason::Manual
    ));
    let mut coordinator =
        RefreshCoordinator::new(RefreshPolicy::default(), RefreshMode::ManualOnly);
    assert_eq!(
        coordinator.request(RefreshReason::Startup, 0).unwrap(),
        RequestDecision::Suppressed
    );
}

#[test]
fn coordinator_keeps_one_in_flight_and_unique_ids() {
    let mut coordinator =
        RefreshCoordinator::new(RefreshPolicy::new(1, 5).unwrap(), RefreshMode::Auto);
    let first = match coordinator.request(RefreshReason::Startup, 0).unwrap() {
        RequestDecision::Started(request) => request,
        other => panic!("unexpected decision: {other:?}"),
    };
    assert_eq!(
        coordinator.request(RefreshReason::Manual, 0).unwrap(),
        RequestDecision::Coalesced {
            active_request_id: first.id
        }
    );
    let (_, actions) = coordinator.complete(first.id, 1, true).unwrap();
    let second = match actions.as_slice() {
        [CoordinatorAction::Started(request)] => *request,
        other => panic!("unexpected actions: {other:?}"),
    };
    assert_ne!(first.id, second.id);
    assert_eq!(
        coordinator.complete(999, 2, true).unwrap().0,
        CompletionOutcome::UnknownRequest
    );
}

#[test]
fn failure_backoff_resets_after_success_and_mode_updates_hot() {
    let mut coordinator =
        RefreshCoordinator::new(RefreshPolicy::new(1, 5).unwrap(), RefreshMode::Auto);
    let request = match coordinator.request(RefreshReason::Manual, 0).unwrap() {
        RequestDecision::Started(value) => value,
        _ => unreachable!(),
    };
    coordinator.complete(request.id, 1, false).unwrap();
    assert_eq!(coordinator.consecutive_failures(), 1);
    coordinator.set_mode(RefreshMode::ManualOnly, 2);
    assert_eq!(coordinator.mode(), RefreshMode::ManualOnly);
    assert_eq!(coordinator.stale_after_secs(), 3_600);
}

#[test]
fn explicit_manual_refresh_bypasses_idle_throttle_but_not_single_in_flight() {
    let mut coordinator =
        RefreshCoordinator::new(RefreshPolicy::new(10, 15).unwrap(), RefreshMode::Auto);
    let startup = match coordinator.request(RefreshReason::Startup, 0).unwrap() {
        RequestDecision::Started(request) => request,
        other => panic!("unexpected decision: {other:?}"),
    };
    coordinator.complete(startup.id, 1, true).unwrap();

    let manual = match coordinator.request(RefreshReason::Manual, 2).unwrap() {
        RequestDecision::Started(request) => request,
        other => panic!("manual refresh was unexpectedly throttled: {other:?}"),
    };
    assert_eq!(
        coordinator.request(RefreshReason::Manual, 2).unwrap(),
        RequestDecision::Coalesced {
            active_request_id: manual.id
        }
    );
    let (_, follow_up) = coordinator.complete(manual.id, 3, true).unwrap();
    assert!(matches!(
        follow_up.as_slice(),
        [CoordinatorAction::Started(request)] if request.reason == RefreshReason::Manual
    ));
}
