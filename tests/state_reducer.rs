use codex_quota_tray::protocol::{RateLimitSnapshot, RateLimitsReadResponse};
use codex_quota_tray::quota::AccountState;
use codex_quota_tray::state::{
    AppStateReducer, AppStateStore, AuthState, DataState, FailureKind, ProcessState,
    STALE_AFTER_SECS, StableDataState, StateEvent, WarningCode,
};
use serde_json::{Value, json};

fn rate_limits_fixture(name: &str) -> RateLimitsReadResponse {
    let contents = match name {
        "single" => include_str!("fixtures/rate_limits_single_weekly.json"),
        "multi" => include_str!("fixtures/rate_limits_multi_bucket.json"),
        "missing" => include_str!("fixtures/rate_limits_missing_fields.json"),
        _ => panic!("unknown fixture"),
    };
    let envelope: Value = serde_json::from_str(contents).unwrap();
    serde_json::from_value(envelope["result"].clone()).unwrap()
}

fn fresh_reducer(at: i64) -> AppStateReducer {
    let mut reducer = AppStateReducer::new();
    reducer.reduce(StateEvent::ProcessReady { generation: 0, at });
    reducer.reduce(StateEvent::AccountUpdated(AccountState::ChatGpt {
        plan_type: Some("plus".to_owned()),
    }));
    reducer.reduce(StateEvent::RefreshStarted { at });
    reducer.reduce(StateEvent::RateLimitsReplaced {
        response: rate_limits_fixture("single"),
        received_at: at,
        source_cli_version: "0.137.0".to_owned(),
    });
    reducer
}

#[test]
fn process_lifecycle_transitions_are_explicit() {
    let mut reducer = AppStateReducer::new();
    assert_eq!(reducer.state().process, ProcessState::Stopped);

    reducer.reduce(StateEvent::ProcessStarting { generation: 3 });
    assert_eq!(
        reducer.state().process,
        ProcessState::Starting { generation: 3 }
    );
    reducer.reduce(StateEvent::ProcessHandshaking { generation: 3 });
    assert_eq!(
        reducer.state().process,
        ProcessState::Handshaking { generation: 3 }
    );
    reducer.reduce(StateEvent::ProcessReady {
        generation: 3,
        at: 100,
    });
    assert_eq!(
        reducer.state().process,
        ProcessState::Ready { generation: 3 }
    );
    reducer.reduce(StateEvent::ProcessBackoff { attempt: 2 });
    assert_eq!(
        reducer.state().process,
        ProcessState::Backoff { attempt: 2 }
    );
    assert_eq!(reducer.state().data, DataState::Offline);
    reducer.reduce(StateEvent::ProcessFailed);
    assert_eq!(reducer.state().process, ProcessState::Failed);
    reducer.reduce(StateEvent::ProcessStopped);
    assert_eq!(reducer.state().process, ProcessState::Stopped);
    assert_eq!(reducer.state().data, DataState::Empty);
}

#[test]
fn refresh_failure_and_staleness_preserve_last_valid_snapshot() {
    let mut reducer = fresh_reducer(100);
    let original = reducer.state().quota.clone();
    assert_eq!(reducer.state().data, DataState::Fresh);

    reducer.reduce(StateEvent::RefreshStarted { at: 200 });
    assert_eq!(
        reducer.state().data,
        DataState::Refreshing {
            previous: StableDataState::Fresh
        }
    );
    assert_eq!(reducer.state().quota, original);

    reducer.reduce(StateEvent::RefreshFailed {
        at: 201,
        kind: FailureKind::Timeout,
    });
    assert_eq!(reducer.state().data, DataState::Fresh);
    assert_eq!(reducer.state().quota, original);

    reducer.reduce(StateEvent::Tick {
        now: 100 + STALE_AFTER_SECS,
    });
    assert_eq!(reducer.state().data, DataState::Stale);
    assert_eq!(reducer.state().quota, original);

    reducer.reduce(StateEvent::RefreshStarted { at: 1_100 });
    assert_eq!(
        reducer.state().data,
        DataState::Refreshing {
            previous: StableDataState::Stale
        }
    );
    reducer.reduce(StateEvent::RefreshFailed {
        at: 1_101,
        kind: FailureKind::Transport,
    });
    assert_eq!(reducer.state().data, DataState::Offline);
    assert_eq!(reducer.state().quota, original);

    reducer.reduce(StateEvent::ProcessReady {
        generation: 1,
        at: 1_102,
    });
    assert_eq!(reducer.state().data, DataState::Stale);
    assert_eq!(reducer.state().quota, original);
}

#[test]
fn successful_refresh_restores_fresh_and_clears_transient_failures() {
    let mut reducer = fresh_reducer(100);
    reducer.reduce(StateEvent::RefreshFailed {
        at: 1_100,
        kind: FailureKind::Protocol,
    });
    assert_eq!(reducer.state().data, DataState::Stale);

    reducer.reduce(StateEvent::RateLimitsReplaced {
        response: rate_limits_fixture("single"),
        received_at: 1_200,
        source_cli_version: "0.137.0".to_owned(),
    });
    assert_eq!(reducer.state().data, DataState::Fresh);
    assert_eq!(reducer.state().last_success_at, Some(1_200));
    assert_eq!(reducer.state().last_failure, None);
    assert!(
        !reducer
            .state()
            .warnings
            .contains(&WarningCode::RefreshFailed(FailureKind::Protocol))
    );
}

#[test]
fn incomplete_refresh_does_not_replace_valid_quota_with_empty_data() {
    let mut reducer = fresh_reducer(100);
    let original = reducer.state().quota.clone();

    reducer.reduce(StateEvent::RateLimitsReplaced {
        response: rate_limits_fixture("missing"),
        received_at: 150,
        source_cli_version: "0.137.0".to_owned(),
    });

    assert_eq!(reducer.state().quota, original);
    assert_eq!(reducer.state().last_success_at, Some(100));
    assert_eq!(reducer.state().data, DataState::Fresh);
    assert_eq!(
        reducer.state().last_failure,
        Some(FailureKind::IncompleteData)
    );
    assert!(
        reducer
            .state()
            .warnings
            .contains(&WarningCode::IncompleteQuota)
    );
}

#[test]
fn explicit_non_chatgpt_account_state_clears_old_quota() {
    let mut reducer = fresh_reducer(100);
    reducer.reduce(StateEvent::AccountUpdated(AccountState::Unauthenticated));

    assert_eq!(reducer.state().auth, AuthState::Unauthenticated);
    assert_eq!(reducer.state().data, DataState::Unavailable);
    assert!(reducer.state().quota.is_none());
    assert!(reducer.state().last_success_at.is_none());
}

#[test]
fn sparse_patch_updates_usage_without_clearing_window_metadata() {
    let mut reducer = fresh_reducer(100);
    let patch: RateLimitSnapshot = serde_json::from_value(json!({
        "limitId": "codex",
        "primary": { "usedPercent": 35 }
    }))
    .unwrap();

    reducer.reduce(StateEvent::RateLimitsPatched {
        patch,
        received_at: 200,
    });

    let quota = reducer.state().quota.as_ref().unwrap();
    assert_eq!(quota.windows[0].used_percent, 35);
    assert_eq!(quota.windows[0].window_duration_mins, Some(10_080));
    assert_eq!(reducer.state().last_success_at, Some(200));
    assert_eq!(reducer.state().data, DataState::Fresh);
}

#[test]
fn ambiguous_sparse_patch_is_rejected_without_guessing_a_bucket() {
    let mut reducer = AppStateReducer::new();
    reducer.reduce(StateEvent::AccountUpdated(AccountState::ChatGpt {
        plan_type: Some("plus".to_owned()),
    }));
    reducer.reduce(StateEvent::RateLimitsReplaced {
        response: rate_limits_fixture("multi"),
        received_at: 100,
        source_cli_version: "0.137.0".to_owned(),
    });
    let original = reducer.state().quota.clone();
    let patch: RateLimitSnapshot = serde_json::from_value(json!({
        "primary": { "usedPercent": 99 }
    }))
    .unwrap();

    reducer.reduce(StateEvent::RateLimitsPatched {
        patch,
        received_at: 200,
    });

    assert_eq!(reducer.state().quota, original);
    assert_eq!(reducer.state().last_success_at, Some(100));
    assert!(
        reducer
            .state()
            .warnings
            .contains(&WarningCode::AmbiguousSparsePatch)
    );
}

#[test]
fn patch_without_baseline_is_a_warning_not_a_synthetic_zero() {
    let mut reducer = AppStateReducer::new();
    let patch: RateLimitSnapshot = serde_json::from_value(json!({
        "primary": { "usedPercent": 50 }
    }))
    .unwrap();
    reducer.reduce(StateEvent::RateLimitsPatched {
        patch,
        received_at: 200,
    });

    assert!(reducer.state().quota.is_none());
    assert!(
        reducer
            .state()
            .warnings
            .contains(&WarningCode::PatchWithoutSnapshot)
    );
}

#[test]
fn state_store_returns_owned_snapshots() {
    let store = AppStateStore::new();
    let returned = store.dispatch(StateEvent::ProcessStarting { generation: 4 });
    assert_eq!(returned.process, ProcessState::Starting { generation: 4 });
    assert_eq!(store.snapshot(), returned);
}
