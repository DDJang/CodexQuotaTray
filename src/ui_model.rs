use chrono::{Datelike, Local, TimeZone};

use crate::quota::{QuotaWindow, ResetCreditsState};
use crate::state::{AppState, AuthState, DataState, FailureKind, ProcessState};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayIconState {
    Normal,
    Caution,
    Critical,
    Exhausted,
    Refreshing,
    Offline,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StatusTone {
    Success,
    Warning,
    Error,
    Refreshing,
    Neutral,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ViewPreferences {
    pub show_remaining_percent: bool,
    pub use_24_hour_time: bool,
}

impl Default for ViewPreferences {
    fn default() -> Self {
        Self {
            show_remaining_percent: true,
            use_24_hour_time: true,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TrayView {
    pub icon: TrayIconState,
    pub tooltip: String,
    pub title: String,
    pub plan_badge: Option<String>,
    pub status: String,
    pub status_line: String,
    pub status_tone: StatusTone,
    pub windows: Vec<QuotaWindowView>,
    pub reset_credits: String,
    pub reset_credit_state: ResetCreditViewState,
    pub refresh_label: String,
    pub can_refresh: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ResetCreditViewState {
    Unavailable,
    Empty,
    CountOnly {
        count: i64,
    },
    PartialDetails {
        count: i64,
        detail_count: usize,
        earliest_expires_at: i64,
    },
    CompleteDetails {
        count: i64,
        earliest_expires_at: i64,
    },
}

impl ResetCreditViewState {
    pub fn text(&self) -> String {
        match self {
            Self::Unavailable => "当前账户未提供重置卡信息".to_owned(),
            Self::Empty => "暂无可用重置卡".to_owned(),
            Self::CountOnly { count } => format!("重置卡 {count} 张 · 到期时间未提供"),
            Self::PartialDetails {
                count,
                earliest_expires_at,
                ..
            } => format!(
                "重置卡 {count} 张 · 最近已知 {}到期",
                format_credit_expiry(*earliest_expires_at)
            ),
            Self::CompleteDetails {
                count,
                earliest_expires_at,
            } => format!(
                "重置卡 {count} 张 · 最早 {}到期",
                format_credit_expiry(*earliest_expires_at)
            ),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaWindowView {
    pub name: String,
    pub percent_value: String,
    pub percent_suffix: String,
    pub progress_percent: u8,
    pub reset_line: String,
    pub remaining_percent: i64,
}

pub fn project_tray_view(state: &AppState, now: i64, preferences: ViewPreferences) -> TrayView {
    let windows = state
        .quota
        .as_ref()
        .map(|quota| {
            quota
                .windows
                .iter()
                .map(|window| project_window(window, now, preferences))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let icon = icon_state(state, &windows);
    let status = status_text(state);
    let tooltip = tooltip_text(state, &windows, &status);
    let (status_line, status_tone) = projected_status(state, now, preferences);
    let refreshing = matches!(state.data, DataState::Refreshing { .. });
    let failed = state.last_failure.is_some();
    let reset_credit_state = project_reset_credits(state);

    TrayView {
        icon,
        tooltip,
        title: "Codex 用量".to_owned(),
        plan_badge: plan_badge(&state.auth),
        status,
        status_line,
        status_tone,
        windows,
        reset_credits: reset_credit_state.text(),
        reset_credit_state,
        refresh_label: if refreshing {
            "正在刷新…".to_owned()
        } else if failed {
            "重试".to_owned()
        } else {
            "刷新".to_owned()
        },
        can_refresh: !refreshing,
    }
}

fn project_window(window: &QuotaWindow, now: i64, preferences: ViewPreferences) -> QuotaWindowView {
    let (display_percent, percent_suffix, progress_percent) = if preferences.show_remaining_percent
    {
        (window.remaining_percent, "剩余", window.remaining_percent)
    } else {
        (window.used_percent, "已用", window.used_percent)
    };
    let reset_line = window.resets_at.map_or_else(
        || "未提供重置时间".to_owned(),
        |timestamp| format_reset_line(timestamp, now, preferences),
    );

    QuotaWindowView {
        name: window_display_name(window),
        percent_value: format!("{display_percent}%"),
        percent_suffix: percent_suffix.to_owned(),
        progress_percent: progress_percent.clamp(0, 100) as u8,
        reset_line,
        remaining_percent: window.remaining_percent,
    }
}

fn icon_state(state: &AppState, windows: &[QuotaWindowView]) -> TrayIconState {
    if !matches!(state.auth, AuthState::Authenticated { .. })
        || matches!(
            state.data,
            DataState::Empty | DataState::Stale | DataState::Offline | DataState::Unavailable
        )
    {
        return TrayIconState::Offline;
    }
    if matches!(state.data, DataState::Refreshing { .. }) {
        return TrayIconState::Refreshing;
    }
    if state
        .quota
        .as_ref()
        .is_some_and(|quota| quota.rate_limit_reached)
    {
        return TrayIconState::Exhausted;
    }
    match windows.iter().map(|window| window.remaining_percent).min() {
        Some(0) => TrayIconState::Exhausted,
        Some(1..=19) => TrayIconState::Critical,
        Some(20..=50) => TrayIconState::Caution,
        Some(_) => TrayIconState::Normal,
        None => TrayIconState::Offline,
    }
}

fn tooltip_text(state: &AppState, windows: &[QuotaWindowView], status: &str) -> String {
    if windows.is_empty() || !matches!(state.auth, AuthState::Authenticated { .. }) {
        return format!("Codex：{status}");
    }
    let mut parts = windows
        .iter()
        .take(2)
        .map(|window| {
            let short_name = window.name.strip_suffix("额度").unwrap_or(&window.name);
            format!("{short_name} {}%", window.remaining_percent)
        })
        .collect::<Vec<_>>();
    parts.push(status.to_owned());
    format!("Codex：{}", parts.join(" · "))
}

fn plan_badge(auth: &AuthState) -> Option<String> {
    match auth {
        AuthState::Authenticated { plan_type } => plan_type.as_deref().map(display_plan_name),
        AuthState::ApiKey => Some("API Key".to_owned()),
        AuthState::Bedrock => Some("Bedrock".to_owned()),
        _ => None,
    }
}

fn display_plan_name(plan: &str) -> String {
    if plan.eq_ignore_ascii_case("plus") {
        "Plus".to_owned()
    } else {
        plan.to_owned()
    }
}

fn projected_status(
    state: &AppState,
    now: i64,
    preferences: ViewPreferences,
) -> (String, StatusTone) {
    if matches!(
        &state.auth,
        AuthState::Unauthenticated
            | AuthState::ApiKey
            | AuthState::Bedrock
            | AuthState::Unsupported(_)
    ) {
        return (status_text(state), StatusTone::Error);
    }
    if matches!(state.data, DataState::Refreshing { .. }) {
        return ("正在刷新…".to_owned(), StatusTone::Refreshing);
    }
    if let Some(failure) = state.last_failure {
        return (failure_status(failure), StatusTone::Error);
    }
    if reset_credit_information_is_partial(state) && matches!(state.data, DataState::Fresh) {
        return ("⚠ 部分额度信息暂不可用".to_owned(), StatusTone::Warning);
    }
    let stale = state
        .last_success_at
        .is_some_and(|last| now.saturating_sub(last) >= state.stale_after_secs.max(15 * 60))
        || matches!(state.data, DataState::Stale);
    if stale {
        let suffix = state.last_success_at.map_or_else(String::new, |timestamp| {
            format!(
                " · 更新于 {}",
                format_status_time(timestamp, now, preferences)
            )
        });
        return (format!("▲ 数据已过期{suffix}"), StatusTone::Warning);
    }
    if matches!(state.data, DataState::Fresh) {
        return (
            state.last_success_at.map_or_else(
                || "● 已更新".to_owned(),
                |timestamp| {
                    format!(
                        "● 更新于 {}",
                        format_status_time(timestamp, now, preferences)
                    )
                },
            ),
            StatusTone::Success,
        );
    }
    (status_text(state), StatusTone::Neutral)
}

fn reset_credit_information_is_partial(state: &AppState) -> bool {
    match state.quota.as_ref().map(|quota| &quota.reset_credits) {
        Some(ResetCreditsState::Unavailable) => true,
        Some(ResetCreditsState::Available {
            available_count,
            detail_count,
            valid_expirations,
        }) => {
            *available_count > 0
                && (valid_expirations.is_empty() || *detail_count < *available_count as usize)
        }
        None => false,
    }
}

fn failure_status(kind: FailureKind) -> String {
    match kind {
        FailureKind::Transport => "! 连接失败，显示上次数据",
        FailureKind::Timeout => "! 请求超时，显示上次数据",
        FailureKind::Rpc => "! 服务端返回错误，显示上次数据",
        FailureKind::Protocol => "! 响应格式不兼容，显示上次数据",
        FailureKind::IncompleteData => "! 额度数据不完整，显示上次数据",
    }
    .to_owned()
}

fn status_text(state: &AppState) -> String {
    match &state.auth {
        AuthState::Unauthenticated => "尚未登录 Codex".to_owned(),
        AuthState::ApiKey => "API Key 模式没有 ChatGPT 套餐额度".to_owned(),
        AuthState::Bedrock => "Bedrock 模式没有 ChatGPT 套餐额度".to_owned(),
        AuthState::Unsupported(_) => "当前账户类型不支持额度显示".to_owned(),
        _ => status_text_for_supported_account(state),
    }
}

fn status_text_for_supported_account(state: &AppState) -> String {
    match state.process {
        ProcessState::Failed if state.quota.is_some() => {
            return "Codex App Server 已停止；显示上次数据".to_owned();
        }
        ProcessState::Failed => {
            return "无法启动 Codex App Server；请确认 Codex CLI 已安装并可运行".to_owned();
        }
        ProcessState::Backoff { .. } => {
            return "Codex App Server 异常；正在有限重试并保留上次数据".to_owned();
        }
        _ => {}
    }
    match state.data {
        DataState::Fresh if state.quota.as_ref().is_some_and(|q| q.rate_limit_reached) => {
            "Codex 服务报告当前额度已达到限制".to_owned()
        }
        DataState::Empty if matches!(state.auth, AuthState::Unknown) => {
            "正在连接 Codex…".to_owned()
        }
        DataState::Empty => "尚无额度数据".to_owned(),
        DataState::Refreshing { .. } => "正在刷新，保留上次数据".to_owned(),
        DataState::Fresh => "数据已更新".to_owned(),
        DataState::Stale => "数据可能已过期".to_owned(),
        DataState::Offline => "暂时无法连接，显示上次数据".to_owned(),
        DataState::Unavailable => "暂时无法获取额度".to_owned(),
    }
}

fn project_reset_credits(state: &AppState) -> ResetCreditViewState {
    match state.quota.as_ref().map(|quota| &quota.reset_credits) {
        Some(ResetCreditsState::Available {
            available_count: 0, ..
        }) => ResetCreditViewState::Empty,
        Some(ResetCreditsState::Available {
            available_count,
            detail_count,
            valid_expirations,
        }) => {
            let Some(earliest_expires_at) = valid_expirations.first().copied() else {
                return ResetCreditViewState::CountOnly {
                    count: *available_count,
                };
            };
            if *detail_count == *available_count as usize {
                ResetCreditViewState::CompleteDetails {
                    count: *available_count,
                    earliest_expires_at,
                }
            } else {
                ResetCreditViewState::PartialDetails {
                    count: *available_count,
                    detail_count: *detail_count,
                    earliest_expires_at,
                }
            }
        }
        Some(ResetCreditsState::Unavailable) | None => ResetCreditViewState::Unavailable,
    }
}

fn format_credit_expiry(timestamp: i64) -> String {
    Local.timestamp_opt(timestamp, 0).single().map_or_else(
        || "未知时间".to_owned(),
        |value| value.format("%-m月%-d日").to_string(),
    )
}

fn format_status_time(timestamp: i64, now: i64, preferences: ViewPreferences) -> String {
    let Some(value) = Local.timestamp_opt(timestamp, 0).single() else {
        return "无效时间".to_owned();
    };
    let same_day = Local.timestamp_opt(now, 0).single().is_some_and(|current| {
        (value.year(), value.ordinal()) == (current.year(), current.ordinal())
    });
    match (same_day, preferences.use_24_hour_time) {
        (true, true) => value.format("%H:%M").to_string(),
        (true, false) => value.format("%I:%M %p").to_string(),
        (false, true) => value.format("%m-%d %H:%M").to_string(),
        (false, false) => value.format("%m-%d %I:%M %p").to_string(),
    }
}

fn format_reset_line(timestamp: i64, now: i64, preferences: ViewPreferences) -> String {
    let Some(value) = Local.timestamp_opt(timestamp, 0).single() else {
        return "无效重置时间".to_owned();
    };
    let absolute = if preferences.use_24_hour_time {
        value.format("%-m月%-d日 %H:%M").to_string()
    } else {
        value.format("%-m月%-d日 %I:%M %p").to_string()
    };
    let remaining = timestamp.saturating_sub(now);
    if remaining <= 0 {
        return format!("{absolute} 重置 · 等待额度数据更新");
    }
    let total_minutes = (remaining + 59) / 60;
    let days = total_minutes / 1_440;
    let hours = (total_minutes % 1_440) / 60;
    let minutes = total_minutes % 60;
    let relative = if days > 0 {
        format!("{days}天{hours}小时")
    } else if hours > 0 {
        format!("{hours}小时{minutes}分钟")
    } else {
        format!("{minutes}分钟")
    };
    format!("{absolute} 重置 · 还有 {relative}")
}

pub fn window_display_name(window: &QuotaWindow) -> String {
    if let Some(name) = window.limit_name.as_deref().filter(|name| !name.is_empty()) {
        return name.to_owned();
    }
    match window.window_duration_mins {
        Some(300) => "5 小时额度".to_owned(),
        Some(10_080) => "7 天额度".to_owned(),
        Some(minutes) if minutes > 0 && minutes % (24 * 60) == 0 => {
            format!("{} 天额度", minutes / (24 * 60))
        }
        Some(minutes) if minutes > 0 && minutes % 60 == 0 => {
            format!("{} 小时额度", minutes / 60)
        }
        Some(minutes) if minutes > 0 => format!("{minutes} 分钟额度"),
        _ => "额度窗口".to_owned(),
    }
}
