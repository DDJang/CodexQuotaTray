use chrono::{Local, TimeZone};

use crate::compatibility::VersionCompatibility;
use crate::quota::QuotaWindow;
use crate::state::{AppState, AuthState, DataState, ProcessState};

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
    pub status: String,
    pub status_line: String,
    pub windows: Vec<QuotaWindowView>,
    pub reset_credits: String,
    pub can_refresh: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaWindowView {
    pub name: String,
    pub percent_label: String,
    pub progress_percent: u8,
    pub reset_countdown: String,
    pub reset_at_label: String,
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
    let status_line = status_line(state, preferences);

    TrayView {
        icon,
        tooltip,
        title: account_title(&state.auth),
        status,
        status_line,
        windows,
        reset_credits: "当前服务端暂未提供重置次数".to_owned(),
        can_refresh: !matches!(state.data, DataState::Refreshing { .. }),
    }
}

fn project_window(window: &QuotaWindow, now: i64, preferences: ViewPreferences) -> QuotaWindowView {
    let display_percent = if preferences.show_remaining_percent {
        window.remaining_percent
    } else {
        window.used_percent
    };
    let percent_label = if preferences.show_remaining_percent {
        format!("{display_percent}% 剩余")
    } else {
        format!("{display_percent}% 已用")
    };
    let progress_percent = if preferences.show_remaining_percent {
        window.remaining_percent
    } else {
        window.used_percent
    }
    .clamp(0, 100) as u8;
    let (reset_countdown, reset_at_label) = window.resets_at.map_or_else(
        || ("未提供重置时间".to_owned(), String::new()),
        |timestamp| {
            (
                format_countdown(timestamp, now),
                format_reset_time(timestamp, preferences),
            )
        },
    );

    QuotaWindowView {
        name: window_display_name(window),
        percent_label,
        progress_percent,
        reset_countdown,
        reset_at_label,
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
    let minimum = windows.iter().map(|window| window.remaining_percent).min();
    match minimum {
        Some(0) => TrayIconState::Exhausted,
        Some(1..=5) => TrayIconState::Critical,
        Some(6..=20) => TrayIconState::Caution,
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
    parts.push(
        if matches!(state.data, DataState::Fresh)
            && state
                .quota
                .as_ref()
                .is_some_and(|quota| quota.rate_limit_reached)
        {
            "服务端报告已达到限制".to_owned()
        } else if matches!(state.data, DataState::Fresh) {
            "重置次数不可用".to_owned()
        } else {
            status.to_owned()
        },
    );
    format!("Codex：{}", parts.join(" · "))
}

fn account_title(auth: &AuthState) -> String {
    match auth {
        AuthState::Authenticated { plan_type } => plan_type.as_ref().map_or_else(
            || "Codex 用量".to_owned(),
            |plan| format!("Codex 用量 · {}", display_plan_name(plan)),
        ),
        AuthState::ApiKey => "Codex · API Key 计费".to_owned(),
        AuthState::Bedrock => "Codex · Amazon Bedrock".to_owned(),
        _ => "Codex 用量".to_owned(),
    }
}

fn display_plan_name(plan: &str) -> &str {
    if plan.eq_ignore_ascii_case("plus") {
        "Plus"
    } else {
        plan
    }
}

fn status_line(state: &AppState, preferences: ViewPreferences) -> String {
    if !matches!(&state.auth, AuthState::Authenticated { .. })
        || !matches!(
            &state.compatibility,
            VersionCompatibility::Unknown | VersionCompatibility::Match { .. }
        )
    {
        return status_text(state);
    }
    match &state.data {
        DataState::Refreshing { .. } => "正在更新…".to_owned(),
        DataState::Fresh => state.last_success_at.map_or_else(
            || "● 已更新".to_owned(),
            |timestamp| format!("● 已更新 · {}", format_status_time(timestamp, preferences)),
        ),
        DataState::Empty if matches!(state.auth, AuthState::Unknown) => "正在连接…".to_owned(),
        DataState::Empty => "● 更新失败 · 点击刷新重试".to_owned(),
        DataState::Stale | DataState::Offline | DataState::Unavailable => {
            "● 更新失败 · 点击刷新重试".to_owned()
        }
    }
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
    match &state.compatibility {
        VersionCompatibility::Mismatch {
            schema_version,
            runtime_version,
        } => {
            return format!(
                "Codex CLI {runtime_version} 与协议基线 {schema_version} 不匹配；只读兼容模式"
            );
        }
        VersionCompatibility::Unreported { .. } => {
            return "无法确认 Codex CLI 版本；只读兼容模式".to_owned();
        }
        VersionCompatibility::Unknown | VersionCompatibility::Match { .. } => {}
    }

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
        DataState::Fresh
            if state
                .quota
                .as_ref()
                .is_some_and(|quota| quota.rate_limit_reached) =>
        {
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

fn format_local_time(timestamp: i64, preferences: ViewPreferences) -> String {
    format_with_pattern(timestamp, preferences, "%m-%d %H:%M", "%m-%d %I:%M %p")
}

fn format_status_time(timestamp: i64, preferences: ViewPreferences) -> String {
    format_local_time(timestamp, preferences)
}

fn format_reset_time(timestamp: i64, preferences: ViewPreferences) -> String {
    format_with_pattern(
        timestamp,
        preferences,
        "%m月%d日 %H:%M",
        "%m月%d日 %I:%M %p",
    )
}

fn format_with_pattern(
    timestamp: i64,
    preferences: ViewPreferences,
    twenty_four_hour: &str,
    twelve_hour: &str,
) -> String {
    let Some(value) = Local.timestamp_opt(timestamp, 0).single() else {
        return "无效时间".to_owned();
    };
    if preferences.use_24_hour_time {
        value.format(twenty_four_hour).to_string()
    } else {
        value.format(twelve_hour).to_string()
    }
}

fn format_countdown(timestamp: i64, now: i64) -> String {
    let remaining = timestamp.saturating_sub(now);
    if remaining <= 0 {
        return "即将重置".to_owned();
    }
    let minutes = (remaining + 59) / 60;
    let days = minutes / (24 * 60);
    let hours = (minutes % (24 * 60)) / 60;
    let minutes = minutes % 60;
    let mut parts = Vec::with_capacity(3);
    if days > 0 {
        parts.push(format!("{days}天"));
    }
    if hours > 0 {
        parts.push(format!("{hours}小时"));
    }
    if minutes > 0 {
        parts.push(format!("{minutes}分钟"));
    }
    format!("{}后重置", parts.join(" "))
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
