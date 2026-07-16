use codex_quota_tray::compatibility::VersionCompatibility;
use codex_quota_tray::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use codex_quota_tray::state::{AppState, AuthState, DataState, ProcessState, StableDataState};
use codex_quota_tray::ui_model::{TrayIconState, ViewPreferences, project_tray_view};

const NOW: i64 = 1_700_000_000;

#[test]
fn weekly_window_is_named_by_duration_and_projects_remaining_percentage() {
    let state = state_with_windows(DataState::Fresh, vec![window(28, Some(10_080))]);
    let view = project_tray_view(&state, NOW, ViewPreferences::default());

    assert_eq!(view.icon, TrayIconState::Normal);
    assert_eq!(view.windows[0].name, "7 天额度");
    assert_eq!(view.windows[0].percent_label, "72% 剩余");
    assert_eq!(view.windows[0].progress_percent, 72);
    assert!(view.tooltip.contains("72%"));
    assert!(view.reset_credits.contains("未提供"));
    assert!(!view.reset_credits.contains("0 次"));
}

#[test]
fn threshold_icons_have_distinct_semantic_states() {
    for (remaining, expected) in [
        (21, TrayIconState::Normal),
        (20, TrayIconState::Caution),
        (5, TrayIconState::Critical),
        (0, TrayIconState::Exhausted),
    ] {
        let state = state_with_windows(DataState::Fresh, vec![window(100 - remaining, Some(300))]);
        assert_eq!(
            project_tray_view(&state, NOW, ViewPreferences::default()).icon,
            expected
        );
    }
}

#[test]
fn refreshing_preserves_windows_and_stale_or_offline_is_visibly_deemphasized() {
    let refreshing = state_with_windows(
        DataState::Refreshing {
            previous: StableDataState::Fresh,
        },
        vec![window(30, Some(300))],
    );
    let view = project_tray_view(&refreshing, NOW, ViewPreferences::default());
    assert_eq!(view.icon, TrayIconState::Refreshing);
    assert_eq!(view.windows.len(), 1);
    assert!(view.status.contains("保留上次数据"));
    assert!(!view.can_refresh);

    for data in [DataState::Stale, DataState::Offline] {
        let state = state_with_windows(data, vec![window(30, Some(300))]);
        let view = project_tray_view(&state, NOW, ViewPreferences::default());
        assert_eq!(view.icon, TrayIconState::Offline);
        assert_eq!(view.windows.len(), 1);
    }
}

#[test]
fn missing_window_and_reset_time_are_not_rendered_as_full_quota() {
    let state = state_with_windows(DataState::Unavailable, Vec::new());
    let view = project_tray_view(&state, NOW, ViewPreferences::default());
    assert_eq!(view.windows, Vec::new());
    assert_eq!(view.icon, TrayIconState::Offline);
    assert!(!view.tooltip.contains("100%"));

    let mut no_reset = window(40, Some(300));
    no_reset.resets_at = None;
    let state = state_with_windows(DataState::Fresh, vec![no_reset]);
    let view = project_tray_view(&state, NOW, ViewPreferences::default());
    assert_eq!(view.windows[0].reset_label, "未提供重置时间");
}

#[test]
fn unauthenticated_and_non_chatgpt_modes_are_actionable_without_fake_quota() {
    for (auth, expected) in [
        (AuthState::Unauthenticated, "尚未登录"),
        (AuthState::ApiKey, "API Key"),
        (AuthState::Bedrock, "Bedrock"),
    ] {
        let state = AppState {
            auth,
            data: DataState::Unavailable,
            ..AppState::default()
        };
        let view = project_tray_view(&state, NOW, ViewPreferences::default());
        assert!(view.status.contains(expected));
        assert!(view.windows.is_empty());
        assert!(!view.tooltip.contains("100%"));
    }
}

#[test]
fn used_percentage_and_twelve_hour_preferences_only_change_presentation() {
    let state = state_with_windows(DataState::Fresh, vec![window(28, Some(300))]);
    let view = project_tray_view(
        &state,
        NOW,
        ViewPreferences {
            show_remaining_percent: false,
            use_24_hour_time: false,
        },
    );
    assert_eq!(view.windows[0].percent_label, "28% 已用");
    assert_eq!(view.windows[0].progress_percent, 28);
    assert!(
        view.windows[0].reset_label.contains("AM") || view.windows[0].reset_label.contains("PM")
    );
}

#[test]
fn failed_app_server_and_backoff_have_actionable_status_without_fake_quota() {
    let failed = AppState {
        process: ProcessState::Failed,
        data: DataState::Unavailable,
        ..AppState::default()
    };
    let failed_view = project_tray_view(&failed, NOW, ViewPreferences::default());
    assert!(failed_view.status.contains("Codex CLI 已安装"));
    assert!(!failed_view.tooltip.contains("100%"));

    let mut backoff = state_with_windows(DataState::Offline, vec![window(40, Some(300))]);
    backoff.process = ProcessState::Backoff { attempt: 2 };
    let backoff_view = project_tray_view(&backoff, NOW, ViewPreferences::default());
    assert!(backoff_view.status.contains("有限重试"));
    assert_eq!(backoff_view.windows.len(), 1);
}

#[test]
fn schema_mismatch_is_visible_without_exposing_raw_user_agent() {
    let mut state = state_with_windows(DataState::Fresh, vec![window(28, Some(300))]);
    state.compatibility = VersionCompatibility::Mismatch {
        schema_version: "0.137.0".to_owned(),
        runtime_version: "0.138.0".to_owned(),
    };
    let view = project_tray_view(&state, NOW, ViewPreferences::default());
    assert!(view.status.contains("0.138.0"));
    assert!(view.status.contains("0.137.0"));
    assert!(view.status.contains("只读兼容模式"));
    assert!(!view.status.contains("userAgent"));
}

#[test]
fn explicit_server_limit_reached_overrides_nonzero_percentage_without_guessing_a_window() {
    let mut state = state_with_windows(DataState::Fresh, vec![window(40, Some(300))]);
    state.quota.as_mut().unwrap().rate_limit_reached = true;

    let view = project_tray_view(&state, NOW, ViewPreferences::default());
    assert_eq!(view.icon, TrayIconState::Exhausted);
    assert!(view.status.contains("服务报告"));
    assert!(view.tooltip.contains("已达到限制"));
    assert_eq!(view.windows[0].remaining_percent, 60);
}

#[test]
fn projection_stays_equal_between_countdown_boundaries() {
    let state = state_with_windows(DataState::Fresh, vec![window(40, Some(300))]);
    let initial = project_tray_view(&state, NOW, ViewPreferences::default());
    let five_seconds_later = project_tray_view(&state, NOW + 5, ViewPreferences::default());
    let one_minute_later = project_tray_view(&state, NOW + 60, ViewPreferences::default());

    assert_eq!(initial, five_seconds_later);
    assert_ne!(
        initial.windows[0].reset_label,
        one_minute_later.windows[0].reset_label
    );
}

fn state_with_windows(data: DataState, windows: Vec<QuotaWindow>) -> AppState {
    AppState {
        auth: AuthState::Authenticated {
            plan_type: Some("plus".to_owned()),
        },
        data,
        quota: Some(QuotaSummary {
            windows,
            issues: Vec::new(),
            reset_credits: ResetCreditsState::UnavailableInSchema,
            rate_limit_reached: false,
        }),
        last_success_at: Some(NOW - 30),
        ..AppState::default()
    }
}

fn window(used_percent: i64, duration: Option<i64>) -> QuotaWindow {
    QuotaWindow {
        limit_id: None,
        limit_name: None,
        source_slot: "primary",
        used_percent,
        remaining_percent: 100 - used_percent,
        window_duration_mins: duration,
        resets_at: Some(NOW + 7_200),
    }
}
