use std::collections::BTreeMap;

use serde::Deserialize;
use serde_json::{Value, json};

pub const INITIALIZE_METHOD: &str = "initialize";
pub const INITIALIZED_METHOD: &str = "initialized";
pub const ACCOUNT_READ_METHOD: &str = "account/read";
pub const RATE_LIMITS_READ_METHOD: &str = "account/rateLimits/read";
pub const RATE_LIMITS_UPDATED_METHOD: &str = "account/rateLimits/updated";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InitializeResponse {
    pub user_agent: String,
    pub platform_family: String,
    pub platform_os: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountReadResponse {
    pub account: Option<AccountInfo>,
    pub requires_openai_auth: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountInfo {
    #[serde(rename = "type")]
    pub kind: String,
    pub plan_type: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RateLimitsReadResponse {
    pub rate_limits: Option<RateLimitSnapshot>,
    pub rate_limits_by_limit_id: Option<BTreeMap<String, RateLimitSnapshot>>,
}

impl RateLimitsReadResponse {
    pub fn merge_sparse_notification(&mut self, patch: RateLimitSnapshot) {
        match self.rate_limits.as_mut() {
            Some(current) => current.merge_sparse(patch.clone()),
            None => self.rate_limits = Some(patch.clone()),
        }

        let Some(buckets) = self.rate_limits_by_limit_id.as_mut() else {
            return;
        };

        let matching_key = patch
            .limit_id
            .as_ref()
            .and_then(|limit_id| {
                buckets
                    .iter()
                    .find(|(key, snapshot)| {
                        *key == limit_id || snapshot.limit_id.as_ref() == Some(limit_id)
                    })
                    .map(|(key, _)| key.clone())
            })
            .or_else(|| {
                (buckets.len() == 1)
                    .then(|| buckets.keys().next().cloned())
                    .flatten()
            });

        if let Some(key) = matching_key {
            if let Some(current) = buckets.get_mut(&key) {
                current.merge_sparse(patch);
            }
        } else if let Some(limit_id) = patch.limit_id.clone() {
            buckets.insert(limit_id, patch);
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RateLimitSnapshot {
    pub credits: Option<CreditsSnapshot>,
    pub limit_id: Option<String>,
    pub limit_name: Option<String>,
    pub plan_type: Option<String>,
    pub primary: Option<RateLimitWindow>,
    pub rate_limit_reached_type: Option<String>,
    pub secondary: Option<RateLimitWindow>,
}

impl RateLimitSnapshot {
    pub fn merge_sparse(&mut self, patch: Self) {
        merge_option(&mut self.limit_id, patch.limit_id);
        merge_option(&mut self.limit_name, patch.limit_name);
        merge_option(&mut self.plan_type, patch.plan_type);
        merge_option(
            &mut self.rate_limit_reached_type,
            patch.rate_limit_reached_type,
        );
        merge_window(&mut self.primary, patch.primary);
        merge_window(&mut self.secondary, patch.secondary);

        if let Some(patch_credits) = patch.credits {
            match self.credits.as_mut() {
                Some(current) => current.merge_sparse(patch_credits),
                None => self.credits = Some(patch_credits),
            }
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct CreditsSnapshot {
    pub has_credits: Option<bool>,
    pub unlimited: Option<bool>,
}

impl CreditsSnapshot {
    fn merge_sparse(&mut self, patch: Self) {
        merge_option(&mut self.has_credits, patch.has_credits);
        merge_option(&mut self.unlimited, patch.unlimited);
    }
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RateLimitWindow {
    pub resets_at: Option<i64>,
    pub used_percent: Option<i64>,
    pub window_duration_mins: Option<i64>,
}

fn merge_window(current: &mut Option<RateLimitWindow>, patch: Option<RateLimitWindow>) {
    let Some(patch) = patch else {
        return;
    };

    match current.as_mut() {
        Some(current) => {
            merge_option(&mut current.resets_at, patch.resets_at);
            merge_option(&mut current.used_percent, patch.used_percent);
            merge_option(
                &mut current.window_duration_mins,
                patch.window_duration_mins,
            );
        }
        None => *current = Some(patch),
    }
}

fn merge_option<T>(current: &mut Option<T>, patch: Option<T>) {
    if patch.is_some() {
        *current = patch;
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountRateLimitsUpdatedNotification {
    pub rate_limits: RateLimitSnapshot,
}

pub fn initialize_params() -> Value {
    json!({
        "clientInfo": {
            "name": "codex_quota_tray_spike",
            "title": "CodexQuotaTray P0 Spike",
            "version": env!("CARGO_PKG_VERSION")
        }
    })
}

pub fn account_read_params() -> Value {
    json!({ "refreshToken": false })
}

pub fn rate_limits_read_params() -> Value {
    Value::Null
}
