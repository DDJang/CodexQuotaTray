use std::sync::{Arc, RwLock, RwLockReadGuard, RwLockWriteGuard};

use crate::compatibility::VersionCompatibility;
use crate::protocol::{RateLimitSnapshot, RateLimitsReadResponse, SparseMergeOutcome};
use crate::quota::{AccountState, QuotaSummary, summarize_rate_limits};

pub const STALE_AFTER_SECS: i64 = 15 * 60;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProcessState {
    Stopped,
    Starting { generation: u64 },
    Handshaking { generation: u64 },
    Ready { generation: u64 },
    Backoff { attempt: u32 },
    Failed,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthState {
    Unknown,
    Authenticated { plan_type: Option<String> },
    Unauthenticated,
    ApiKey,
    Bedrock,
    Unsupported(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StableDataState {
    Empty,
    Fresh,
    Stale,
    Offline,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DataState {
    Empty,
    Refreshing { previous: StableDataState },
    Fresh,
    Stale,
    Offline,
    Unavailable,
}

impl DataState {
    fn stable(&self) -> StableDataState {
        match self {
            Self::Empty => StableDataState::Empty,
            Self::Refreshing { previous } => *previous,
            Self::Fresh => StableDataState::Fresh,
            Self::Stale => StableDataState::Stale,
            Self::Offline => StableDataState::Offline,
            Self::Unavailable => StableDataState::Unavailable,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailureKind {
    Transport,
    Timeout,
    Rpc,
    Protocol,
    IncompleteData,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WarningCode {
    RefreshFailed(FailureKind),
    IncompleteQuota,
    QuotaDataIssues,
    PatchWithoutSnapshot,
    AmbiguousSparsePatch,
    SchemaVersionMismatch,
    RuntimeVersionUnreported,
    PersistenceFailure,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppState {
    pub process: ProcessState,
    pub compatibility: VersionCompatibility,
    pub auth: AuthState,
    pub data: DataState,
    pub quota: Option<QuotaSummary>,
    pub last_success_at: Option<i64>,
    pub last_attempt_at: Option<i64>,
    pub source_cli_version: Option<String>,
    pub rate_limits_read_succeeded: bool,
    pub reset_credits_field_present: bool,
    pub last_failure: Option<FailureKind>,
    pub stale_after_secs: i64,
    pub warnings: Vec<WarningCode>,
}

impl Default for AppState {
    fn default() -> Self {
        Self {
            process: ProcessState::Stopped,
            compatibility: VersionCompatibility::Unknown,
            auth: AuthState::Unknown,
            data: DataState::Empty,
            quota: None,
            last_success_at: None,
            last_attempt_at: None,
            source_cli_version: None,
            rate_limits_read_succeeded: false,
            reset_credits_field_present: false,
            last_failure: None,
            stale_after_secs: STALE_AFTER_SECS,
            warnings: Vec::new(),
        }
    }
}

#[derive(Debug, Clone)]
pub enum StateEvent {
    ProcessStarting {
        generation: u64,
    },
    ProcessHandshaking {
        generation: u64,
    },
    ProcessReady {
        generation: u64,
        at: i64,
    },
    ProcessBackoff {
        attempt: u32,
    },
    ProcessFailed,
    ProcessStopped,
    CompatibilityChecked(VersionCompatibility),
    CachedQuotaRestored {
        summary: QuotaSummary,
        last_success_at: i64,
        source_cli_version: Option<String>,
    },
    PersistenceFailed,
    RefreshStarted {
        at: i64,
    },
    AccountUpdated(AccountState),
    RateLimitsReplaced {
        response: RateLimitsReadResponse,
        received_at: i64,
        source_cli_version: Option<String>,
    },
    RateLimitsPatched {
        patch: RateLimitSnapshot,
        received_at: i64,
    },
    RefreshFailed {
        at: i64,
        kind: FailureKind,
    },
    Tick {
        now: i64,
    },
    StaleThresholdUpdated {
        seconds: i64,
        now: i64,
    },
}

#[derive(Debug, Default)]
pub struct AppStateReducer {
    state: AppState,
    rate_limits: Option<RateLimitsReadResponse>,
}

impl AppStateReducer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn state(&self) -> &AppState {
        &self.state
    }

    pub fn reduce(&mut self, event: StateEvent) -> &AppState {
        match event {
            StateEvent::ProcessStarting { generation } => {
                self.state.process = ProcessState::Starting { generation };
            }
            StateEvent::ProcessHandshaking { generation } => {
                self.state.process = ProcessState::Handshaking { generation };
            }
            StateEvent::ProcessReady { generation, at } => {
                self.state.process = ProcessState::Ready { generation };
                if self.state.data == DataState::Offline {
                    self.state.data = self.data_state_for_age(at);
                }
            }
            StateEvent::ProcessBackoff { attempt } => {
                self.state.process = ProcessState::Backoff { attempt };
                self.state.data = DataState::Offline;
            }
            StateEvent::ProcessFailed => {
                self.state.process = ProcessState::Failed;
                self.state.data = if self.state.quota.is_some() {
                    DataState::Offline
                } else {
                    DataState::Unavailable
                };
            }
            StateEvent::ProcessStopped => {
                self.state.process = ProcessState::Stopped;
                self.state.data = if self.state.quota.is_some() {
                    DataState::Stale
                } else {
                    DataState::Empty
                };
            }
            StateEvent::CompatibilityChecked(compatibility) => {
                self.update_compatibility(compatibility)
            }
            StateEvent::CachedQuotaRestored {
                summary,
                last_success_at,
                source_cli_version,
            } => {
                if !summary.windows.is_empty() {
                    self.state.quota = Some(summary);
                    self.state.last_success_at = Some(last_success_at);
                    self.state.source_cli_version = source_cli_version;
                    self.state.data = DataState::Stale;
                }
            }
            StateEvent::PersistenceFailed => self.push_warning(WarningCode::PersistenceFailure),
            StateEvent::RefreshStarted { at } => {
                self.state.last_attempt_at = Some(at);
                self.state.data = DataState::Refreshing {
                    previous: self.state.data.stable(),
                };
            }
            StateEvent::AccountUpdated(account) => self.update_account(account),
            StateEvent::RateLimitsReplaced {
                response,
                received_at,
                source_cli_version,
            } => self.replace_rate_limits(response, received_at, source_cli_version),
            StateEvent::RateLimitsPatched { patch, received_at } => {
                self.patch_rate_limits(patch, received_at)
            }
            StateEvent::RefreshFailed { at, kind } => self.refresh_failed(at, kind),
            StateEvent::Tick { now } => self.tick(now),
            StateEvent::StaleThresholdUpdated { seconds, now } => {
                self.state.stale_after_secs = seconds.max(STALE_AFTER_SECS);
                let stale = self.is_stale(now);
                self.state.data = match self.state.data.clone() {
                    DataState::Fresh | DataState::Stale => self.data_state_for_age(now),
                    DataState::Refreshing {
                        previous: StableDataState::Fresh | StableDataState::Stale,
                    } => DataState::Refreshing {
                        previous: if stale {
                            StableDataState::Stale
                        } else {
                            StableDataState::Fresh
                        },
                    },
                    other => other,
                };
            }
        }
        &self.state
    }

    fn update_account(&mut self, account: AccountState) {
        self.state.auth = match account {
            AccountState::ChatGpt { plan_type } => AuthState::Authenticated { plan_type },
            AccountState::ApiKey => AuthState::ApiKey,
            AccountState::AmazonBedrock => AuthState::Bedrock,
            AccountState::Unauthenticated => AuthState::Unauthenticated,
            AccountState::Unavailable => AuthState::Unknown,
            AccountState::Unsupported(kind) => AuthState::Unsupported(kind),
        };

        if !matches!(self.state.auth, AuthState::Authenticated { .. }) {
            self.rate_limits = None;
            self.state.quota = None;
            self.state.last_success_at = None;
            self.state.source_cli_version = None;
            self.state.data = DataState::Unavailable;
        }
    }

    fn replace_rate_limits(
        &mut self,
        response: RateLimitsReadResponse,
        received_at: i64,
        source_cli_version: Option<String>,
    ) {
        self.state.rate_limits_read_succeeded = true;
        self.state.reset_credits_field_present = response.rate_limit_reset_credits.is_some();
        let summary = summarize_rate_limits(&response);
        if summary.windows.is_empty() {
            self.push_warning(WarningCode::IncompleteQuota);
            self.refresh_failed(received_at, FailureKind::IncompleteData);
            return;
        }

        self.clear_transient_warnings();
        if !summary.issues.is_empty() {
            self.push_warning(WarningCode::QuotaDataIssues);
        }
        self.rate_limits = Some(response);
        self.state.quota = Some(summary);
        self.state.last_success_at = Some(received_at);
        self.state.last_attempt_at = Some(received_at);
        self.state.source_cli_version = source_cli_version;
        self.state.last_failure = None;
        self.state.data = DataState::Fresh;
    }

    fn patch_rate_limits(&mut self, patch: RateLimitSnapshot, received_at: i64) {
        let Some(current) = self.rate_limits.as_ref() else {
            if patch_is_self_contained(&patch) {
                self.replace_rate_limits(
                    RateLimitsReadResponse {
                        rate_limit_reset_credits: None,
                        rate_limits: Some(patch),
                        rate_limits_by_limit_id: None,
                    },
                    received_at,
                    self.state.source_cli_version.clone(),
                );
                return;
            }
            self.push_warning(WarningCode::PatchWithoutSnapshot);
            return;
        };
        let mut updated = current.clone();
        if updated.merge_sparse_notification(patch) == SparseMergeOutcome::AmbiguousBucket {
            self.push_warning(WarningCode::AmbiguousSparsePatch);
            return;
        }

        let summary = summarize_rate_limits(&updated);
        if summary.windows.is_empty() {
            self.push_warning(WarningCode::IncompleteQuota);
            return;
        }
        self.clear_transient_warnings();
        if !summary.issues.is_empty() {
            self.push_warning(WarningCode::QuotaDataIssues);
        }
        self.rate_limits = Some(updated);
        self.state.quota = Some(summary);
        self.state.last_success_at = Some(received_at);
        self.state.last_failure = None;
        self.state.data = DataState::Fresh;
    }

    fn refresh_failed(&mut self, at: i64, kind: FailureKind) {
        self.state.last_attempt_at = Some(at);
        self.state.last_failure = Some(kind);
        self.push_warning(WarningCode::RefreshFailed(kind));
        self.state.data = if kind == FailureKind::Transport {
            DataState::Offline
        } else {
            self.data_state_for_age(at)
        };
    }

    fn tick(&mut self, now: i64) {
        if !self.is_stale(now) {
            return;
        }
        self.state.data = match self.state.data.clone() {
            DataState::Fresh => DataState::Stale,
            DataState::Refreshing {
                previous: StableDataState::Fresh,
            } => DataState::Refreshing {
                previous: StableDataState::Stale,
            },
            other => other,
        };
    }

    fn data_state_for_age(&self, now: i64) -> DataState {
        if self.state.quota.is_none() {
            DataState::Unavailable
        } else if self.is_stale(now) {
            DataState::Stale
        } else {
            DataState::Fresh
        }
    }

    fn is_stale(&self, now: i64) -> bool {
        self.state
            .last_success_at
            .is_some_and(|success| now.saturating_sub(success) >= self.state.stale_after_secs)
    }

    fn push_warning(&mut self, warning: WarningCode) {
        if !self.state.warnings.contains(&warning) {
            self.state.warnings.push(warning);
        }
    }

    fn update_compatibility(&mut self, compatibility: VersionCompatibility) {
        self.state.warnings.retain(|warning| {
            !matches!(
                warning,
                WarningCode::SchemaVersionMismatch | WarningCode::RuntimeVersionUnreported
            )
        });
        match compatibility {
            VersionCompatibility::Mismatch { .. } => {
                self.push_warning(WarningCode::SchemaVersionMismatch)
            }
            VersionCompatibility::Unreported { .. } => {
                self.push_warning(WarningCode::RuntimeVersionUnreported)
            }
            VersionCompatibility::Unknown | VersionCompatibility::Match { .. } => {}
        }
        self.state.compatibility = compatibility;
    }

    fn clear_transient_warnings(&mut self) {
        self.state.warnings.retain(|warning| {
            !matches!(
                warning,
                WarningCode::RefreshFailed(_)
                    | WarningCode::IncompleteQuota
                    | WarningCode::QuotaDataIssues
                    | WarningCode::PatchWithoutSnapshot
                    | WarningCode::AmbiguousSparsePatch
            )
        });
    }
}

fn patch_is_self_contained(patch: &RateLimitSnapshot) -> bool {
    [patch.primary.as_ref(), patch.secondary.as_ref()]
        .into_iter()
        .flatten()
        .any(|window| {
            window.used_percent.is_some()
                && window.window_duration_mins.is_some_and(|value| value > 0)
                && window.resets_at.is_some_and(|value| value > 0)
        })
}

#[derive(Clone, Default)]
pub struct AppStateStore {
    reducer: Arc<RwLock<AppStateReducer>>,
}

impl AppStateStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn dispatch(&self, event: StateEvent) -> AppState {
        write_unpoisoned(&self.reducer).reduce(event).clone()
    }

    pub fn snapshot(&self) -> AppState {
        read_unpoisoned(&self.reducer).state().clone()
    }
}

fn read_unpoisoned<T>(lock: &RwLock<T>) -> RwLockReadGuard<'_, T> {
    lock.read().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn write_unpoisoned<T>(lock: &RwLock<T>) -> RwLockWriteGuard<'_, T> {
    lock.write()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}
