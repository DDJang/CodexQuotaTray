use std::collections::{HashMap, HashSet};

use sha2::{Digest, Sha256};

use crate::persistence::{
    ALERT_STATE_SCHEMA_VERSION, NotificationSettings, PersistedAlertState, PersistedAlertWindow,
};
use crate::quota::{QuotaSummary, QuotaWindow};
use crate::ui_model::window_display_name;

pub const ALERT_THRESHOLDS: [u8; 3] = [50, 20, 10];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AlertKind {
    Remaining50,
    Remaining20,
    Remaining10,
}

impl AlertKind {
    pub fn threshold(self) -> u8 {
        match self {
            Self::Remaining50 => 50,
            Self::Remaining20 => 20,
            Self::Remaining10 => 10,
        }
    }

    fn from_threshold(threshold: u8) -> Option<Self> {
        match threshold {
            50 => Some(Self::Remaining50),
            20 => Some(Self::Remaining20),
            10 => Some(Self::Remaining10),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaAlert {
    pub kind: AlertKind,
    pub window_name: String,
    pub remaining_percent: i64,
    pub resets_at: Option<i64>,
}

#[derive(Debug, Clone)]
struct WindowAlertState {
    duration_mins: Option<i64>,
    remaining_percent: i64,
    resets_at: Option<i64>,
    handled: HashSet<u8>,
}

#[derive(Debug, Clone, Default)]
pub struct AlertObservation {
    pub alerts: Vec<QuotaAlert>,
    pub changed: bool,
}

#[derive(Debug, Default)]
pub struct AlertTracker {
    windows: HashMap<String, WindowAlertState>,
    pending_baselines: HashSet<u8>,
}

impl AlertTracker {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_persisted(state: PersistedAlertState) -> Self {
        let windows = state
            .windows
            .into_iter()
            .map(|window| {
                (
                    window.pseudonymous_key,
                    WindowAlertState {
                        duration_mins: window.window_duration_mins,
                        remaining_percent: window.last_remaining_percent,
                        resets_at: window.resets_at_utc,
                        handled: window.handled_thresholds.into_iter().collect(),
                    },
                )
            })
            .collect();
        Self {
            windows,
            pending_baselines: state.baseline_thresholds.into_iter().collect(),
        }
    }

    pub fn snapshot(&self) -> PersistedAlertState {
        let mut windows = self
            .windows
            .iter()
            .map(|(key, window)| {
                let mut handled_thresholds = window.handled.iter().copied().collect::<Vec<_>>();
                handled_thresholds.sort_unstable();
                PersistedAlertWindow {
                    pseudonymous_key: key.clone(),
                    window_duration_mins: window.duration_mins,
                    resets_at_utc: window.resets_at,
                    last_remaining_percent: window.remaining_percent,
                    handled_thresholds,
                }
            })
            .collect::<Vec<_>>();
        windows.sort_by(|left, right| left.pseudonymous_key.cmp(&right.pseudonymous_key));
        let mut baseline_thresholds = self.pending_baselines.iter().copied().collect::<Vec<_>>();
        baseline_thresholds.sort_unstable();
        PersistedAlertState {
            schema_version: ALERT_STATE_SCHEMA_VERSION,
            baseline_thresholds,
            windows,
        }
    }

    pub fn observe(
        &mut self,
        quota: &QuotaSummary,
        settings: &NotificationSettings,
    ) -> AlertObservation {
        let mut observation = AlertObservation::default();
        let mut observed = HashSet::new();
        let identities = pseudonymous_window_keys(&quota.windows);
        let pending = self.pending_baselines.clone();
        let mut consumed_pending = false;

        for (window, key) in quota.windows.iter().zip(identities) {
            observed.insert(key.clone());
            if !window.percentage_valid || !(0..=100).contains(&window.remaining_percent) {
                continue;
            }
            let Some(previous) = self.windows.get_mut(&key) else {
                let mut handled = HashSet::new();
                for threshold in ALERT_THRESHOLDS {
                    if window.remaining_percent <= i64::from(threshold) {
                        handled.insert(threshold);
                    }
                }
                handled.extend(pending.iter().copied());
                self.windows.insert(
                    key,
                    WindowAlertState {
                        duration_mins: window.window_duration_mins,
                        remaining_percent: window.remaining_percent,
                        resets_at: window.resets_at,
                        handled,
                    },
                );
                consumed_pending = true;
                observation.changed = true;
                continue;
            };

            if is_new_cycle(
                previous.resets_at,
                window.resets_at,
                window.window_duration_mins,
                previous.remaining_percent,
                window.remaining_percent,
            ) {
                previous.handled.clear();
                observation.changed = true;
            }

            if !pending.is_empty() {
                previous.handled.extend(pending.iter().copied());
                consumed_pending = true;
                observation.changed = true;
            }

            let crossed = ALERT_THRESHOLDS
                .into_iter()
                .filter(|threshold| {
                    crossed_threshold(
                        previous.remaining_percent,
                        window.remaining_percent,
                        *threshold,
                    )
                })
                .collect::<Vec<_>>();
            if !crossed.is_empty() {
                let urgent = crossed.iter().copied().min().unwrap_or(10);
                let should_alert = enabled(urgent, settings) && !previous.handled.contains(&urgent);
                previous.handled.extend(crossed);
                if should_alert && let Some(kind) = AlertKind::from_threshold(urgent) {
                    observation.alerts.push(QuotaAlert {
                        kind,
                        window_name: window_display_name(window),
                        remaining_percent: window.remaining_percent,
                        resets_at: window.resets_at,
                    });
                }
                observation.changed = true;
            }
            if previous.remaining_percent != window.remaining_percent
                || previous.resets_at != window.resets_at
                || previous.duration_mins != window.window_duration_mins
            {
                observation.changed = true;
            }
            previous.remaining_percent = window.remaining_percent;
            previous.resets_at = window.resets_at;
            previous.duration_mins = window.window_duration_mins;
        }

        if consumed_pending {
            self.pending_baselines.clear();
        }
        let before = self.windows.len();
        self.windows.retain(|key, _| observed.contains(key));
        observation.changed |= self.windows.len() != before;
        observation
    }

    /// Applies the non-retroactive enable rule. A reliable current snapshot is marked handled;
    /// otherwise the next reliable snapshot becomes a silent baseline.
    pub fn enable_threshold(&mut self, threshold: u8, quota: Option<&QuotaSummary>) -> bool {
        if !ALERT_THRESHOLDS.contains(&threshold) {
            return false;
        }
        let Some(quota) = quota else {
            return self.pending_baselines.insert(threshold);
        };
        let keys = pseudonymous_window_keys(&quota.windows);
        let mut reliable = false;
        let mut changed = false;
        for (window, key) in quota.windows.iter().zip(keys) {
            if !window.percentage_valid || !(0..=100).contains(&window.remaining_percent) {
                continue;
            }
            reliable = true;
            if window.remaining_percent <= i64::from(threshold) {
                if let Some(state) = self.windows.get_mut(&key) {
                    changed |= state.handled.insert(threshold);
                } else {
                    let mut handled = HashSet::new();
                    handled.insert(threshold);
                    self.windows.insert(
                        key,
                        WindowAlertState {
                            duration_mins: window.window_duration_mins,
                            remaining_percent: window.remaining_percent,
                            resets_at: window.resets_at,
                            handled,
                        },
                    );
                    changed = true;
                }
            }
        }
        if !reliable {
            changed |= self.pending_baselines.insert(threshold);
        }
        changed
    }
}

pub fn crossed_threshold(previous: i64, current: i64, threshold: u8) -> bool {
    previous > i64::from(threshold) && current <= i64::from(threshold)
}

pub fn is_new_cycle(
    previous_reset: Option<i64>,
    current_reset: Option<i64>,
    duration_mins: Option<i64>,
    previous_remaining: i64,
    current_remaining: i64,
) -> bool {
    if let (Some(previous), Some(current)) = (previous_reset, current_reset) {
        let duration_secs = duration_mins
            .filter(|minutes| *minutes > 0)
            .unwrap_or(10)
            .saturating_mul(60);
        let tolerance = 5_i64.saturating_mul(60).max(duration_secs / 2);
        return current.saturating_sub(previous) >= tolerance;
    }
    current_remaining >= 80 && current_remaining.saturating_sub(previous_remaining) >= 50
}

fn enabled(threshold: u8, settings: &NotificationSettings) -> bool {
    match threshold {
        50 => settings.remaining_50_percent,
        20 => settings.remaining_20_percent,
        10 => settings.remaining_10_percent,
        _ => false,
    }
}

fn pseudonymous_window_keys(windows: &[QuotaWindow]) -> Vec<String> {
    let mut fallback_counts = HashMap::<String, usize>::new();
    windows
        .iter()
        .map(|window| {
            if let Some(limit_id) = window.limit_id.as_deref().filter(|value| !value.is_empty()) {
                let digest = Sha256::digest(limit_id.as_bytes());
                return format!("sha256:{digest:x}");
            }
            let base = format!(
                "fallback:{}:{}",
                window.source_slot,
                window.window_duration_mins.unwrap_or_default()
            );
            let occurrence = fallback_counts.entry(base.clone()).or_default();
            let key = format!("{base}:{}", *occurrence);
            *occurrence += 1;
            key
        })
        .collect()
}
