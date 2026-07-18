use codex_quota_tray::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use codex_quota_tray::state::{AppState, AuthState, DataState, FailureKind};
use codex_quota_tray::ui_model::{
    ResetCreditViewState, StatusTone, ViewPreferences, project_tray_view,
};

#[test]
fn title_badge_and_percentage_are_separate_display_fields() {
    let state = state_at(72, 1_700_090_000, 1_700_000_000);
    let view = project_tray_view(&state, 1_700_000_100, ViewPreferences::default());
    assert_eq!(view.title, "Codex 用量");
    assert_eq!(view.plan_badge.as_deref(), Some("Plus"));
    assert_eq!(view.windows[0].percent_value, "72%");
    assert_eq!(view.windows[0].percent_suffix, "剩余");
    assert!(view.windows[0].reset_line.contains("重置 · 还有"));
    assert!(view.reset_credits.starts_with("重置卡 2 张"));
    assert!(matches!(
        view.reset_credit_state,
        ResetCreditViewState::CompleteDetails { count: 2, .. }
    ));
}

#[test]
fn display_preference_does_not_change_original_remaining() {
    let state = state_at(72, 1_700_090_000, 1_700_000_000);
    let view = project_tray_view(
        &state,
        1_700_000_100,
        ViewPreferences {
            show_remaining_percent: false,
            use_24_hour_time: true,
        },
    );
    assert_eq!(view.windows[0].percent_value, "28%");
    assert_eq!(view.windows[0].percent_suffix, "已用");
    assert_eq!(view.windows[0].remaining_percent, 72);
}

#[test]
fn status_priority_refresh_failure_stale_then_fresh() {
    let mut state = state_at(72, 1_700_090_000, 1_700_000_000);
    let fresh = project_tray_view(&state, 1_700_000_100, ViewPreferences::default());
    assert_eq!(fresh.status_tone, StatusTone::Success);
    assert!(fresh.status_line.contains("更新于"));

    state.data = DataState::Stale;
    let stale = project_tray_view(&state, 1_700_004_000, ViewPreferences::default());
    assert_eq!(stale.status_tone, StatusTone::Warning);
    assert!(stale.status_line.contains("过期"));

    state.last_failure = Some(FailureKind::Timeout);
    let failed = project_tray_view(&state, 1_700_004_000, ViewPreferences::default());
    assert_eq!(failed.refresh_label, "重试");
    assert_eq!(failed.status_line, "! 请求超时，显示上次数据");

    state.data = DataState::Refreshing {
        previous: codex_quota_tray::state::StableDataState::Stale,
    };
    let refreshing = project_tray_view(&state, 1_700_004_000, ViewPreferences::default());
    assert_eq!(refreshing.status_line, "正在刷新…");
    assert_eq!(refreshing.refresh_label, "正在刷新…");
    assert!(!refreshing.can_refresh);
}

#[test]
fn reset_relative_precision_matches_boundaries() {
    let now = 1_700_000_000;
    for (seconds, expected) in [
        (6 * 86_400 + 23 * 3_600 + 42 * 60, "6天23小时"),
        (5 * 3_600 + 23 * 60, "5小时23分钟"),
        (23 * 60, "23分钟"),
    ] {
        let view = project_tray_view(
            &state_at(50, now + seconds, now),
            now,
            ViewPreferences::default(),
        );
        assert!(view.windows[0].reset_line.contains(expected));
    }
    let expired = project_tray_view(&state_at(50, now, now), now, ViewPreferences::default());
    assert!(expired.windows[0].reset_line.contains("等待额度数据更新"));
}

#[test]
fn reset_credit_view_states_use_authoritative_count_and_local_expiry() {
    let mut state = state_at(80, 1_800_000_000, 1_700_000_000);
    state.quota.as_mut().unwrap().reset_credits = ResetCreditsState::Available {
        available_count: 3,
        detail_count: 1,
        valid_expirations: vec![1_800_000_000],
    };
    let partial = project_tray_view(&state, 1_700_000_100, ViewPreferences::default());
    assert!(matches!(
        partial.reset_credit_state,
        ResetCreditViewState::PartialDetails {
            count: 3,
            detail_count: 1,
            ..
        }
    ));
    assert!(partial.reset_credits.contains("最近已知"));

    state.quota.as_mut().unwrap().reset_credits = ResetCreditsState::Unavailable;
    let unavailable = project_tray_view(&state, 1_700_000_100, ViewPreferences::default());
    assert_eq!(unavailable.status_line, "⚠ 部分额度信息暂不可用");
    assert_eq!(unavailable.status_tone, StatusTone::Warning);
    assert_eq!(unavailable.reset_credits, "当前账户未提供重置卡信息");
}

fn state_at(remaining: i64, resets_at: i64, updated_at: i64) -> AppState {
    AppState {
        auth: AuthState::Authenticated {
            plan_type: Some("plus".to_owned()),
        },
        data: DataState::Fresh,
        quota: Some(QuotaSummary {
            windows: vec![QuotaWindow {
                limit_id: Some("fixture".to_owned()),
                limit_name: None,
                source_slot: "primary",
                used_percent: 100 - remaining,
                remaining_percent: remaining,
                percentage_valid: true,
                window_duration_mins: Some(10_080),
                resets_at: Some(resets_at),
            }],
            issues: vec![],
            reset_credits: ResetCreditsState::Available {
                available_count: 2,
                detail_count: 2,
                valid_expirations: vec![resets_at + 86_400, resets_at + 172_800],
            },
            rate_limit_reached: false,
        }),
        last_success_at: Some(updated_at),
        ..AppState::default()
    }
}
