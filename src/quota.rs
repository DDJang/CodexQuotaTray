use chrono::{Local, TimeZone};

use crate::protocol::{
    AccountReadResponse, RateLimitSnapshot, RateLimitWindow, RateLimitsReadResponse,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AccountState {
    ChatGpt { plan_type: Option<String> },
    ApiKey,
    AmazonBedrock,
    Unauthenticated,
    Unavailable,
    Unsupported(String),
}

pub fn account_state(response: &AccountReadResponse) -> AccountState {
    let Some(account) = response.account.as_ref() else {
        return if response.requires_openai_auth {
            AccountState::Unauthenticated
        } else {
            AccountState::Unavailable
        };
    };

    match account.kind.as_str() {
        "chatgpt" => AccountState::ChatGpt {
            plan_type: account.plan_type.clone(),
        },
        "apiKey" => AccountState::ApiKey,
        "amazonBedrock" => AccountState::AmazonBedrock,
        other => AccountState::Unsupported(other.to_owned()),
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ResetCreditsState {
    UnavailableInSchema,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaWindow {
    pub limit_id: Option<String>,
    pub limit_name: Option<String>,
    pub source_slot: &'static str,
    pub used_percent: i64,
    pub remaining_percent: i64,
    pub window_duration_mins: Option<i64>,
    pub resets_at: Option<i64>,
}

impl QuotaWindow {
    pub fn display_name(&self) -> String {
        let duration = duration_name(self.window_duration_mins);
        match self.limit_name.as_deref() {
            Some(name) if !name.trim().is_empty() => format!("{name} ({duration})"),
            _ => format!("{duration} quota"),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParseIssue {
    pub context: String,
    pub detail: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaSummary {
    pub windows: Vec<QuotaWindow>,
    pub issues: Vec<ParseIssue>,
    pub reset_credits: ResetCreditsState,
    pub rate_limit_reached: bool,
}

pub fn summarize_rate_limits(response: &RateLimitsReadResponse) -> QuotaSummary {
    let mut windows = Vec::new();
    let mut issues = Vec::new();
    let mut rate_limit_reached = false;

    if let Some(buckets) = response
        .rate_limits_by_limit_id
        .as_ref()
        .filter(|buckets| !buckets.is_empty())
    {
        for (bucket_id, snapshot) in buckets {
            rate_limit_reached |= snapshot.rate_limit_reached_type.is_some();
            append_snapshot(
                snapshot,
                Some(bucket_id.as_str()),
                &mut windows,
                &mut issues,
            );
        }
    } else if let Some(snapshot) = response.rate_limits.as_ref() {
        rate_limit_reached = snapshot.rate_limit_reached_type.is_some();
        append_snapshot(snapshot, None, &mut windows, &mut issues);
    } else {
        issues.push(ParseIssue {
            context: "account/rateLimits/read".to_owned(),
            detail: "response omitted both rateLimits and rateLimitsByLimitId".to_owned(),
        });
    }

    QuotaSummary {
        windows,
        issues,
        reset_credits: ResetCreditsState::UnavailableInSchema,
        rate_limit_reached,
    }
}

fn append_snapshot(
    snapshot: &RateLimitSnapshot,
    bucket_id: Option<&str>,
    windows: &mut Vec<QuotaWindow>,
    issues: &mut Vec<ParseIssue>,
) {
    let effective_limit_id = snapshot
        .limit_id
        .clone()
        .or_else(|| bucket_id.map(str::to_owned));

    append_window(
        snapshot,
        effective_limit_id.clone(),
        "primary",
        snapshot.primary.as_ref(),
        windows,
        issues,
    );
    append_window(
        snapshot,
        effective_limit_id,
        "secondary",
        snapshot.secondary.as_ref(),
        windows,
        issues,
    );
}

fn append_window(
    snapshot: &RateLimitSnapshot,
    effective_limit_id: Option<String>,
    source_slot: &'static str,
    window: Option<&RateLimitWindow>,
    windows: &mut Vec<QuotaWindow>,
    issues: &mut Vec<ParseIssue>,
) {
    let Some(window) = window else {
        return;
    };

    let context = format!(
        "{}.{source_slot}",
        effective_limit_id.as_deref().unwrap_or("unnamed-limit")
    );
    let Some(raw_used_percent) = window.used_percent else {
        issues.push(ParseIssue {
            context,
            detail: "window omitted required usedPercent; it was not replaced with zero".to_owned(),
        });
        return;
    };

    if !(0..=100).contains(&raw_used_percent) {
        issues.push(ParseIssue {
            context,
            detail: format!(
                "usedPercent {raw_used_percent} was outside 0..=100 and was clamped for display"
            ),
        });
    }

    let used_percent = raw_used_percent.clamp(0, 100);
    windows.push(QuotaWindow {
        limit_id: effective_limit_id,
        limit_name: snapshot.limit_name.clone(),
        source_slot,
        used_percent,
        remaining_percent: 100 - used_percent,
        window_duration_mins: window.window_duration_mins,
        resets_at: window.resets_at,
    });
}

pub fn duration_name(minutes: Option<i64>) -> String {
    match minutes {
        Some(300) => "5-hour".to_owned(),
        Some(10_080) => "7-day".to_owned(),
        Some(value) if value > 0 && value % 1_440 == 0 => {
            format!("{}-day", value / 1_440)
        }
        Some(value) if value > 0 && value % 60 == 0 => format!("{}-hour", value / 60),
        Some(value) if value > 0 => format!("{value}-minute"),
        Some(value) => format!("invalid-duration({value}m)"),
        None => "unknown-duration".to_owned(),
    }
}

pub fn format_reset_time(timestamp: Option<i64>) -> String {
    let Some(timestamp) = timestamp else {
        return "not provided".to_owned();
    };

    match Local.timestamp_opt(timestamp, 0).single() {
        Some(value) => value.format("%Y-%m-%d %H:%M:%S %Z").to_string(),
        None => "invalid timestamp".to_owned(),
    }
}
