use serde::{Deserialize, Serialize};

pub const MINIMUM_STALE_SECS: u64 = 15 * 60;
pub const MANUAL_ONLY_STALE_SECS: u64 = 60 * 60;

#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum RefreshMode {
    #[default]
    Auto,
    Every5Minutes,
    Every15Minutes,
    Every30Minutes,
    ManualOnly,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RefreshPolicy {
    pub minimum_interval_secs: u64,
    pub request_timeout_secs: u64,
}

impl RefreshPolicy {
    pub fn new(minimum_interval_secs: u64, request_timeout_secs: u64) -> Result<Self, String> {
        if minimum_interval_secs == 0 || request_timeout_secs == 0 {
            return Err("refresh interval and timeout must be positive".to_owned());
        }
        Ok(Self {
            minimum_interval_secs,
            request_timeout_secs,
        })
    }
}

impl Default for RefreshPolicy {
    fn default() -> Self {
        Self {
            minimum_interval_secs: 10,
            request_timeout_secs: 15,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RefreshReason {
    Startup,
    Manual,
    RateLimitNotification,
    Resume,
    NetworkRestored,
    CardOpened,
    Scheduled,
}

impl RefreshReason {
    fn priority(self) -> u8 {
        match self {
            Self::Startup => 7,
            Self::Manual => 6,
            Self::Resume => 5,
            Self::NetworkRestored => 4,
            Self::RateLimitNotification => 3,
            Self::CardOpened => 2,
            Self::Scheduled => 1,
        }
    }
}

pub fn reason_allowed(mode: RefreshMode, reason: RefreshReason) -> bool {
    mode != RefreshMode::ManualOnly || reason == RefreshReason::Manual
}

pub fn base_interval_secs(mode: RefreshMode, remaining_percent: Option<i64>) -> Option<u64> {
    match mode {
        RefreshMode::Auto => Some(match remaining_percent {
            Some(value) if value > 50 => 30 * 60,
            Some(value) if value > 20 => 15 * 60,
            Some(_) | None => 5 * 60,
        }),
        RefreshMode::Every5Minutes => Some(5 * 60),
        RefreshMode::Every15Minutes => Some(15 * 60),
        RefreshMode::Every30Minutes => Some(30 * 60),
        RefreshMode::ManualOnly => None,
    }
}

pub fn failure_backoff_secs(consecutive_failures: u32) -> u64 {
    match consecutive_failures {
        0 | 1 => 60,
        2 => 2 * 60,
        3 => 5 * 60,
        _ => 15 * 60,
    }
}

pub fn effective_interval_secs(
    mode: RefreshMode,
    remaining_percent: Option<i64>,
    consecutive_failures: u32,
) -> u64 {
    if mode == RefreshMode::ManualOnly {
        return MANUAL_ONLY_STALE_SECS;
    }
    let base = base_interval_secs(mode, remaining_percent).unwrap_or(5 * 60);
    if consecutive_failures == 0 {
        return base;
    }
    let backoff = failure_backoff_secs(consecutive_failures);
    if mode == RefreshMode::Auto {
        backoff
    } else {
        base.max(backoff)
    }
}

pub fn stale_after_secs(
    mode: RefreshMode,
    remaining_percent: Option<i64>,
    consecutive_failures: u32,
) -> u64 {
    if mode == RefreshMode::ManualOnly {
        MANUAL_ONLY_STALE_SECS
    } else {
        MINIMUM_STALE_SECS.max(
            effective_interval_secs(mode, remaining_percent, consecutive_failures)
                .saturating_mul(2),
        )
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RefreshRequest {
    pub id: u64,
    pub reason: RefreshReason,
    pub started_at: u64,
    pub deadline: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestDecision {
    Started(RefreshRequest),
    Coalesced { active_request_id: u64 },
    Deferred { not_before: u64 },
    Suppressed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoordinatorAction {
    Started(RefreshRequest),
    TimedOut(RefreshRequest),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompletionOutcome {
    Completed,
    UnknownRequest,
}

#[derive(Debug, Clone)]
pub struct RefreshCoordinator {
    policy: RefreshPolicy,
    mode: RefreshMode,
    next_id: u64,
    in_flight: Option<RefreshRequest>,
    pending_reason: Option<RefreshReason>,
    last_started_at: Option<u64>,
    next_scheduled_at: Option<u64>,
    remaining_percent: Option<i64>,
    consecutive_failures: u32,
}

impl RefreshCoordinator {
    pub fn new(policy: RefreshPolicy, mode: RefreshMode) -> Self {
        Self {
            policy,
            mode,
            next_id: 0,
            in_flight: None,
            pending_reason: None,
            last_started_at: None,
            next_scheduled_at: None,
            remaining_percent: None,
            consecutive_failures: 0,
        }
    }

    pub fn request(&mut self, reason: RefreshReason, now: u64) -> Result<RequestDecision, String> {
        if !reason_allowed(self.mode, reason) {
            return Ok(RequestDecision::Suppressed);
        }
        if let Some(active) = self.in_flight {
            self.merge_pending(reason);
            return Ok(RequestDecision::Coalesced {
                active_request_id: active.id,
            });
        }
        if reason != RefreshReason::Manual
            && let Some(not_before) = self.not_before()
            && now < not_before
        {
            self.merge_pending(reason);
            return Ok(RequestDecision::Deferred { not_before });
        }
        self.start(reason, now).map(RequestDecision::Started)
    }

    pub fn set_mode(&mut self, mode: RefreshMode, now: u64) {
        self.mode = mode;
        self.pending_reason = self
            .pending_reason
            .filter(|reason| reason_allowed(mode, *reason));
        self.reanchor_schedule(now);
    }

    pub fn set_remaining_percent(&mut self, remaining_percent: Option<i64>) {
        self.remaining_percent = remaining_percent.filter(|value| (0..=100).contains(value));
    }

    pub fn complete(
        &mut self,
        request_id: u64,
        now: u64,
        succeeded: bool,
    ) -> Result<(CompletionOutcome, Vec<CoordinatorAction>), String> {
        let Some(active) = self.in_flight else {
            return Ok((CompletionOutcome::UnknownRequest, Vec::new()));
        };
        if active.id != request_id {
            return Ok((CompletionOutcome::UnknownRequest, Vec::new()));
        }
        self.in_flight = None;
        if succeeded {
            self.consecutive_failures = 0;
        } else {
            self.consecutive_failures = self.consecutive_failures.saturating_add(1);
        }
        self.reanchor_schedule(now);
        let actions = self.start_due_work(now)?;
        Ok((CompletionOutcome::Completed, actions))
    }

    pub fn tick(&mut self, now: u64) -> Result<Vec<CoordinatorAction>, String> {
        let mut actions = Vec::new();
        if let Some(active) = self.in_flight
            && now >= active.deadline
        {
            self.in_flight = None;
            self.consecutive_failures = self.consecutive_failures.saturating_add(1);
            self.reanchor_schedule(now);
            actions.push(CoordinatorAction::TimedOut(active));
        }
        actions.extend(self.start_due_work(now)?);
        Ok(actions)
    }

    pub fn in_flight(&self) -> Option<RefreshRequest> {
        self.in_flight
    }

    pub fn pending_reason(&self) -> Option<RefreshReason> {
        self.pending_reason
    }

    pub fn mode(&self) -> RefreshMode {
        self.mode
    }

    pub fn consecutive_failures(&self) -> u32 {
        self.consecutive_failures
    }

    pub fn effective_interval_secs(&self) -> u64 {
        effective_interval_secs(self.mode, self.remaining_percent, self.consecutive_failures)
    }

    pub fn stale_after_secs(&self) -> u64 {
        stale_after_secs(self.mode, self.remaining_percent, self.consecutive_failures)
    }

    fn start_due_work(&mut self, now: u64) -> Result<Vec<CoordinatorAction>, String> {
        if self.in_flight.is_some() {
            return Ok(Vec::new());
        }
        let reason = if let Some(reason) = self.pending_reason {
            Some(reason)
        } else if self.next_scheduled_at.is_some_and(|due| now >= due)
            && self.mode != RefreshMode::ManualOnly
        {
            Some(RefreshReason::Scheduled)
        } else {
            None
        };
        let Some(reason) = reason else {
            return Ok(Vec::new());
        };
        if !reason_allowed(self.mode, reason) {
            self.pending_reason = None;
            return Ok(Vec::new());
        }
        if reason != RefreshReason::Manual
            && self.not_before().is_some_and(|not_before| now < not_before)
        {
            return Ok(Vec::new());
        }
        self.pending_reason = None;
        self.next_scheduled_at = None;
        Ok(vec![CoordinatorAction::Started(self.start(reason, now)?)])
    }

    fn start(&mut self, reason: RefreshReason, now: u64) -> Result<RefreshRequest, String> {
        let request_id = self.next_id;
        self.next_id = self
            .next_id
            .checked_add(1)
            .ok_or_else(|| "refresh request IDs were exhausted".to_owned())?;
        let request = RefreshRequest {
            id: request_id,
            reason,
            started_at: now,
            deadline: now.saturating_add(self.policy.request_timeout_secs),
        };
        self.in_flight = Some(request);
        self.last_started_at = Some(now);
        Ok(request)
    }

    fn not_before(&self) -> Option<u64> {
        self.last_started_at
            .map(|last| last.saturating_add(self.policy.minimum_interval_secs))
    }

    fn reanchor_schedule(&mut self, now: u64) {
        self.next_scheduled_at = base_interval_secs(self.mode, self.remaining_percent).map(|_| {
            now.saturating_add(effective_interval_secs(
                self.mode,
                self.remaining_percent,
                self.consecutive_failures,
            ))
        });
    }

    fn merge_pending(&mut self, reason: RefreshReason) {
        if !reason_allowed(self.mode, reason) {
            return;
        }
        self.pending_reason = match self.pending_reason {
            Some(current) if current.priority() >= reason.priority() => Some(current),
            _ => Some(reason),
        };
    }
}

impl Default for RefreshCoordinator {
    fn default() -> Self {
        Self::new(RefreshPolicy::default(), RefreshMode::default())
    }
}
