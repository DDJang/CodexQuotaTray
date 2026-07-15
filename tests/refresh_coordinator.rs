use codex_quota_tray::refresh::{
    CompletionOutcome, CoordinatorAction, RefreshCoordinator, RefreshPolicy, RefreshReason,
    RequestDecision,
};

fn started(decision: RequestDecision) -> codex_quota_tray::refresh::RefreshRequest {
    let RequestDecision::Started(request) = decision else {
        panic!("expected refresh to start");
    };
    request
}

#[test]
fn default_policy_matches_product_limits() {
    let policy = RefreshPolicy::default();
    assert_eq!(policy.minimum_interval_secs, 10);
    assert_eq!(policy.fallback_interval_secs, 600);
    assert_eq!(policy.request_timeout_secs, 15);
}

#[test]
fn requests_are_unique_and_only_one_is_in_flight() {
    let mut coordinator = RefreshCoordinator::default();
    let first = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    assert_eq!(first.id, 0);

    for now in 1..=100 {
        assert_eq!(
            coordinator.request(RefreshReason::RateLimitNotification, now),
            Ok(RequestDecision::Coalesced {
                active_request_id: first.id
            })
        );
        assert_eq!(coordinator.in_flight(), Some(first));
    }
}

#[test]
fn coalesced_requests_keep_the_highest_priority_reason() {
    let mut coordinator = RefreshCoordinator::default();
    let first = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    coordinator.request(RefreshReason::Fallback, 1).unwrap();
    coordinator.request(RefreshReason::Manual, 2).unwrap();
    coordinator
        .request(RefreshReason::RateLimitNotification, 3)
        .unwrap();
    assert_eq!(coordinator.pending_reason(), Some(RefreshReason::Manual));

    let (outcome, actions) = coordinator.complete(first.id, 10, true).unwrap();
    assert_eq!(outcome, CompletionOutcome::Completed);
    let [CoordinatorAction::Started(second)] = actions.as_slice() else {
        panic!("coalesced request should start after minimum interval");
    };
    assert_eq!(second.id, 1);
    assert_eq!(second.reason, RefreshReason::Manual);
}

#[test]
fn minimum_interval_defers_manual_refresh_without_dropping_it() {
    let mut coordinator = RefreshCoordinator::default();
    let first = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    coordinator.complete(first.id, 1, true).unwrap();

    assert_eq!(
        coordinator.request(RefreshReason::Manual, 5),
        Ok(RequestDecision::Deferred { not_before: 10 })
    );
    assert!(coordinator.tick(9).unwrap().is_empty());
    let actions = coordinator.tick(10).unwrap();
    let [CoordinatorAction::Started(request)] = actions.as_slice() else {
        panic!("deferred refresh should start at not_before");
    };
    assert_eq!(request.reason, RefreshReason::Manual);
}

#[test]
fn network_restore_burst_is_coalesced_and_rate_limited() {
    let mut coordinator = RefreshCoordinator::default();
    let startup = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    coordinator.complete(startup.id, 1, true).unwrap();

    for now in 2..10 {
        assert_eq!(
            coordinator.request(RefreshReason::NetworkRestored, now),
            Ok(RequestDecision::Deferred { not_before: 10 })
        );
        assert_eq!(
            coordinator.pending_reason(),
            Some(RefreshReason::NetworkRestored)
        );
    }

    assert!(coordinator.tick(9).unwrap().is_empty());
    let actions = coordinator.tick(10).unwrap();
    let [CoordinatorAction::Started(restored)] = actions.as_slice() else {
        panic!("one coalesced restore refresh should start after the minimum interval");
    };
    assert_eq!(restored.reason, RefreshReason::NetworkRestored);
}

#[test]
fn request_timeout_clears_only_the_matching_in_flight_work() {
    let mut coordinator = RefreshCoordinator::default();
    let request = started(coordinator.request(RefreshReason::Startup, 100).unwrap());
    assert!(coordinator.tick(114).unwrap().is_empty());
    assert_eq!(
        coordinator.tick(115).unwrap(),
        vec![CoordinatorAction::TimedOut(request)]
    );
    assert!(coordinator.in_flight().is_none());
}

#[test]
fn unknown_or_duplicate_completion_does_not_mutate_active_request() {
    let mut coordinator = RefreshCoordinator::default();
    let request = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    assert_eq!(
        coordinator.complete(999, 1, true).unwrap(),
        (CompletionOutcome::UnknownRequest, Vec::new())
    );
    assert_eq!(coordinator.in_flight(), Some(request));
    assert_eq!(
        coordinator.complete(request.id, 1, true).unwrap().0,
        CompletionOutcome::Completed
    );
    assert_eq!(
        coordinator.complete(request.id, 2, true).unwrap().0,
        CompletionOutcome::UnknownRequest
    );
}

#[test]
fn fallback_refresh_is_low_frequency() {
    let mut coordinator = RefreshCoordinator::default();
    let request = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    coordinator.complete(request.id, 1, true).unwrap();

    assert!(coordinator.tick(600).unwrap().is_empty());
    let actions = coordinator.tick(601).unwrap();
    let [CoordinatorAction::Started(fallback)] = actions.as_slice() else {
        panic!("fallback should start ten minutes after last success");
    };
    assert_eq!(fallback.reason, RefreshReason::Fallback);
}

#[test]
fn virtual_24_hour_soak_never_has_more_than_one_request() {
    let mut coordinator = RefreshCoordinator::default();
    let startup = started(coordinator.request(RefreshReason::Startup, 0).unwrap());
    coordinator.complete(startup.id, 0, true).unwrap();
    let mut starts = 1_usize;

    for now in 1..=24 * 60 * 60 {
        let actions = coordinator.tick(now).unwrap();
        for action in actions {
            match action {
                CoordinatorAction::Started(request) => {
                    starts += 1;
                    assert_eq!(coordinator.in_flight(), Some(request));
                    assert_eq!(
                        coordinator.complete(request.id, now, true).unwrap().0,
                        CompletionOutcome::Completed
                    );
                }
                CoordinatorAction::TimedOut(_) => panic!("instant completions must not time out"),
            }
        }
    }

    assert_eq!(starts, 145);
    assert!(coordinator.in_flight().is_none());
    assert_eq!(coordinator.pending_reason(), None);
    assert_eq!(coordinator.last_success_at(), Some(24 * 60 * 60));
}

#[test]
fn invalid_refresh_policy_is_rejected() {
    assert!(RefreshPolicy::new(0, 600, 15).is_err());
    assert!(RefreshPolicy::new(601, 600, 15).is_err());
}
