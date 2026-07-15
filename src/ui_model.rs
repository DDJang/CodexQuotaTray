use chrono::{Local, TimeZone};

use crate::quota::{QuotaWindow, duration_name};
use crate::state::{AppState, AuthState, DataState};

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
    pub windows: Vec<QuotaWindowView>,
    pub reset_credits: String,
    pub last_updated: String,
    pub can_refresh: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaWindowView {
    pub name: String,
    pub percent_label: String,
    pub progress_percent: u8,
    pub reset_label: String,
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
    let last_updated = state.last_success_at.map_or_else(
        || "尚无成功更新".to_owned(),
        |timestamp| format!("最后更新 {}", format_local_time(timestamp, preferences)),
    );

    TrayView {
        icon,
        tooltip,
        title: account_title(&state.auth),
        status,
        windows,
        reset_credits: "重置次数：当前 App Server 版本未提供".to_owned(),
        last_updated,
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
    let reset_label = window.resets_at.map_or_else(
        || "未提供重置时间".to_owned(),
        |timestamp| {
            format!(
                "{} 重置 · {}",
                format_local_time(timestamp, preferences),
                format_countdown(timestamp, now)
            )
        },
    );

    QuotaWindowView {
        name: window.display_name(),
        percent_label,
        progress_percent,
        reset_label,
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
            let short_name = window
                .name
                .strip_suffix(" quota")
                .unwrap_or(&window.name)
                .replace("-hour", "小时")
                .replace("-day", "天");
            format!("{short_name} {}%", window.remaining_percent)
        })
        .collect::<Vec<_>>();
    parts.push(if matches!(state.data, DataState::Fresh) {
        "重置次数不可用".to_owned()
    } else {
        status.to_owned()
    });
    format!("Codex：{}", parts.join(" · "))
}

fn account_title(auth: &AuthState) -> String {
    match auth {
        AuthState::Authenticated { plan_type } => plan_type.as_ref().map_or_else(
            || "Codex 用量".to_owned(),
            |plan| format!("Codex 用量 · {plan}"),
        ),
        AuthState::ApiKey => "Codex · API Key 计费".to_owned(),
        AuthState::Bedrock => "Codex · Amazon Bedrock".to_owned(),
        _ => "Codex 用量".to_owned(),
    }
}

fn status_text(state: &AppState) -> String {
    match &state.auth {
        AuthState::Unauthenticated => "尚未登录 Codex".to_owned(),
        AuthState::ApiKey => "API Key 模式没有 ChatGPT 套餐额度".to_owned(),
        AuthState::Bedrock => "Bedrock 模式没有 ChatGPT 套餐额度".to_owned(),
        AuthState::Unsupported(_) => "当前账户类型不支持额度显示".to_owned(),
        AuthState::Unknown if matches!(state.data, DataState::Empty) => {
            "正在连接 Codex…".to_owned()
        }
        _ => match state.data {
            DataState::Empty => "尚无额度数据".to_owned(),
            DataState::Refreshing { .. } => "正在刷新，保留上次数据".to_owned(),
            DataState::Fresh => "数据已更新".to_owned(),
            DataState::Stale => "数据可能已过期".to_owned(),
            DataState::Offline => "暂时无法连接，显示上次数据".to_owned(),
            DataState::Unavailable => "暂时无法获取额度".to_owned(),
        },
    }
}

fn format_local_time(timestamp: i64, preferences: ViewPreferences) -> String {
    let Some(value) = Local.timestamp_opt(timestamp, 0).single() else {
        return "无效时间".to_owned();
    };
    if preferences.use_24_hour_time {
        value.format("%m-%d %H:%M").to_string()
    } else {
        value.format("%m-%d %I:%M %p").to_string()
    }
}

fn format_countdown(timestamp: i64, now: i64) -> String {
    let remaining = timestamp.saturating_sub(now);
    if remaining <= 0 {
        return "等待服务端更新".to_owned();
    }
    let minutes = (remaining + 59) / 60;
    let days = minutes / (24 * 60);
    let hours = (minutes % (24 * 60)) / 60;
    let minutes = minutes % 60;
    if days > 0 {
        format!("还有 {days}天{hours}小时")
    } else if hours > 0 {
        format!("还有 {hours}小时{minutes}分钟")
    } else {
        format!("还有 {minutes}分钟")
    }
}

pub fn fallback_window_name(duration_mins: Option<i64>) -> String {
    format!("{} quota", duration_name(duration_mins))
}
