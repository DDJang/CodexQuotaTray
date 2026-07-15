use std::collections::{HashMap, HashSet};

use crate::persistence::NotificationSettings;
use crate::quota::{QuotaSummary, QuotaWindow};
use crate::ui_model::window_display_name;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AlertKind {
    Remaining20,
    Remaining5,
    Exhausted,
    Recovered,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuotaAlert {
    pub kind: AlertKind,
    pub window_name: String,
    pub remaining_percent: i64,
    pub resets_at: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct WindowKey {
    limit_id: Option<String>,
    source_slot: &'static str,
    duration_mins: Option<i64>,
}

impl From<&QuotaWindow> for WindowKey {
    fn from(window: &QuotaWindow) -> Self {
        Self {
            limit_id: window.limit_id.clone(),
            source_slot: window.source_slot,
            duration_mins: window.window_duration_mins,
        }
    }
}

#[derive(Debug, Clone)]
struct WindowAlertState {
    remaining_percent: i64,
    resets_at: Option<i64>,
    delivered: HashSet<AlertKind>,
}

#[derive(Debug, Default)]
pub struct AlertTracker {
    windows: HashMap<WindowKey, WindowAlertState>,
}

impl AlertTracker {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn observe(
        &mut self,
        quota: &QuotaSummary,
        settings: &NotificationSettings,
    ) -> Vec<QuotaAlert> {
        let mut alerts = Vec::new();
        let mut observed = HashSet::new();
        for window in &quota.windows {
            let key = WindowKey::from(window);
            observed.insert(key.clone());
            let Some(previous) = self.windows.get_mut(&key) else {
                self.windows.insert(
                    key,
                    WindowAlertState {
                        remaining_percent: window.remaining_percent,
                        resets_at: window.resets_at,
                        delivered: HashSet::new(),
                    },
                );
                continue;
            };

            let new_cycle = previous.resets_at != window.resets_at
                || (previous.remaining_percent <= 20 && window.remaining_percent > 20);
            if new_cycle {
                let should_recover = settings.recovered
                    && window.remaining_percent > previous.remaining_percent
                    && previous.remaining_percent <= 20;
                previous.delivered.clear();
                if should_recover {
                    alerts.push(alert(window, AlertKind::Recovered));
                }
            }

            let severe = crossed_threshold(previous.remaining_percent, window.remaining_percent);
            if let Some(kind) = severe {
                if enabled(kind, settings) && !previous.delivered.contains(&kind) {
                    alerts.push(alert(window, kind));
                }
                mark_crossed(&mut previous.delivered, window.remaining_percent);
            }
            previous.remaining_percent = window.remaining_percent;
            previous.resets_at = window.resets_at;
        }
        self.windows.retain(|key, _| observed.contains(key));
        alerts
    }
}

fn crossed_threshold(previous: i64, current: i64) -> Option<AlertKind> {
    if previous > 0 && current == 0 {
        Some(AlertKind::Exhausted)
    } else if previous > 5 && current <= 5 {
        Some(AlertKind::Remaining5)
    } else if previous > 20 && current <= 20 {
        Some(AlertKind::Remaining20)
    } else {
        None
    }
}

fn mark_crossed(delivered: &mut HashSet<AlertKind>, remaining_percent: i64) {
    if remaining_percent <= 20 {
        delivered.insert(AlertKind::Remaining20);
    }
    if remaining_percent <= 5 {
        delivered.insert(AlertKind::Remaining5);
    }
    if remaining_percent == 0 {
        delivered.insert(AlertKind::Exhausted);
    }
}

fn enabled(kind: AlertKind, settings: &NotificationSettings) -> bool {
    match kind {
        AlertKind::Remaining20 => settings.remaining_20_percent,
        AlertKind::Remaining5 => settings.remaining_5_percent,
        AlertKind::Exhausted => settings.exhausted,
        AlertKind::Recovered => settings.recovered,
    }
}

fn alert(window: &QuotaWindow, kind: AlertKind) -> QuotaAlert {
    QuotaAlert {
        kind,
        window_name: window_display_name(window),
        remaining_percent: window.remaining_percent,
        resets_at: window.resets_at,
    }
}
