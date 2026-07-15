#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RefreshPolicy {
    pub minimum_interval_secs: u64,
    pub fallback_interval_secs: u64,
    pub request_timeout_secs: u64,
}

impl RefreshPolicy {
    pub fn new(
        minimum_interval_secs: u64,
        fallback_interval_secs: u64,
        request_timeout_secs: u64,
    ) -> Result<Self, String> {
        if minimum_interval_secs == 0 || fallback_interval_secs == 0 || request_timeout_secs == 0 {
            return Err("refresh intervals and timeout must be positive".to_owned());
        }
        if minimum_interval_secs > fallback_interval_secs {
            return Err("minimum refresh interval must not exceed fallback interval".to_owned());
        }
        Ok(Self {
            minimum_interval_secs,
            fallback_interval_secs,
            request_timeout_secs,
        })
    }
}

impl Default for RefreshPolicy {
    fn default() -> Self {
        Self {
            minimum_interval_secs: 10,
            fallback_interval_secs: 10 * 60,
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
    Fallback,
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
            Self::Fallback => 1,
        }
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
    next_id: u64,
    in_flight: Option<RefreshRequest>,
    pending_reason: Option<RefreshReason>,
    last_started_at: Option<u64>,
    last_success_at: Option<u64>,
}

impl RefreshCoordinator {
    pub fn new(policy: RefreshPolicy) -> Self {
        Self {
            policy,
            next_id: 0,
            in_flight: None,
            pending_reason: None,
            last_started_at: None,
            last_success_at: None,
        }
    }

    pub fn request(&mut self, reason: RefreshReason, now: u64) -> Result<RequestDecision, String> {
        if let Some(active) = self.in_flight {
            self.merge_pending(reason);
            return Ok(RequestDecision::Coalesced {
                active_request_id: active.id,
            });
        }

        if let Some(not_before) = self.not_before()
            && now < not_before
        {
            self.merge_pending(reason);
            return Ok(RequestDecision::Deferred { not_before });
        }

        self.start(reason, now).map(RequestDecision::Started)
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
            self.last_success_at = Some(now);
        }
        let actions = self.start_due_work(now)?;
        Ok((CompletionOutcome::Completed, actions))
    }

    pub fn tick(&mut self, now: u64) -> Result<Vec<CoordinatorAction>, String> {
        let mut actions = Vec::new();
        if let Some(active) = self.in_flight
            && now >= active.deadline
        {
            self.in_flight = None;
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

    pub fn last_success_at(&self) -> Option<u64> {
        self.last_success_at
    }

    fn start_due_work(&mut self, now: u64) -> Result<Vec<CoordinatorAction>, String> {
        if self.in_flight.is_some() {
            return Ok(Vec::new());
        }

        let reason = if let Some(reason) = self.pending_reason {
            Some(reason)
        } else if self
            .fallback_anchor()
            .is_some_and(|last| now.saturating_sub(last) >= self.policy.fallback_interval_secs)
        {
            Some(RefreshReason::Fallback)
        } else {
            None
        };
        let Some(reason) = reason else {
            return Ok(Vec::new());
        };

        if self.not_before().is_some_and(|not_before| now < not_before) {
            return Ok(Vec::new());
        }

        self.pending_reason = None;
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

    fn fallback_anchor(&self) -> Option<u64> {
        match (self.last_started_at, self.last_success_at) {
            (Some(started), Some(success)) => Some(started.max(success)),
            (Some(started), None) => Some(started),
            (None, Some(success)) => Some(success),
            (None, None) => None,
        }
    }

    fn merge_pending(&mut self, reason: RefreshReason) {
        self.pending_reason = match self.pending_reason {
            Some(current) if current.priority() >= reason.priority() => Some(current),
            _ => Some(reason),
        };
    }
}

impl Default for RefreshCoordinator {
    fn default() -> Self {
        Self::new(RefreshPolicy::default())
    }
}
