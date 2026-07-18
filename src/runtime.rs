use std::collections::VecDeque;
use std::ffi::OsString;
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::de::DeserializeOwned;
use serde_json::Value;

use crate::app_server::AppServerLaunch;
use crate::compatibility::{evaluate_user_agent, schema_codex_version};
use crate::json_rpc::{ClientEvent, JsonRpcClient, RpcClientError};
use crate::persistence::QuotaCacheStore;
use crate::protocol::{
    ACCOUNT_READ_METHOD, AccountRateLimitsUpdatedNotification, AccountReadResponse,
    INITIALIZE_METHOD, INITIALIZED_METHOD, InitializeResponse, RATE_LIMITS_READ_METHOD,
    RATE_LIMITS_UPDATED_METHOD, RateLimitsReadResponse, account_read_params, initialize_params,
    rate_limits_read_params,
};
use crate::quota::{AccountState, account_state};
use crate::refresh::{
    CoordinatorAction, RefreshCoordinator, RefreshMode, RefreshPolicy, RefreshReason,
    RefreshRequest, RequestDecision,
};
use crate::state::{AppState, AppStateStore, FailureKind, StateEvent};
use crate::supervisor::{AppServerSupervisor, RestartPolicy, SupervisorEvent, SupervisorReport};

const INITIALIZE_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Clone)]
pub struct RuntimeConfig {
    pub launch: AppServerLaunch,
    pub restart_policy: RestartPolicy,
    pub refresh_policy: RefreshPolicy,
    pub refresh_mode: RefreshMode,
    pub poll_interval: Duration,
    pub expected_schema_version: String,
    pub quota_cache: Option<QuotaCacheStore>,
}

impl RuntimeConfig {
    pub fn codex(explicit_binary: Option<OsString>) -> Self {
        Self {
            launch: AppServerLaunch::codex(explicit_binary),
            restart_policy: RestartPolicy::default(),
            refresh_policy: RefreshPolicy::default(),
            refresh_mode: RefreshMode::Auto,
            poll_interval: Duration::from_millis(250),
            expected_schema_version: schema_codex_version().to_owned(),
            quota_cache: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RuntimeExitReason {
    ShutdownRequested,
    SupervisorExhausted,
    FatalProtocol,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeReport {
    pub exit_reason: RuntimeExitReason,
    pub supervisor: SupervisorReport,
    pub refresh_successes: usize,
    pub refresh_failures: usize,
    pub rate_limit_notifications: usize,
    pub protocol_diagnostics: usize,
    pub persistence_failures: usize,
}

enum RuntimeCommand {
    Refresh(RefreshReason),
    SetRefreshMode(RefreshMode),
    Shutdown,
}

pub struct QuotaRuntime {
    state: AppStateStore,
    command_sender: Sender<RuntimeCommand>,
    thread: Option<JoinHandle<Result<RuntimeReport, String>>>,
    shutdown_report: Option<RuntimeReport>,
}

impl QuotaRuntime {
    pub fn start(config: RuntimeConfig) -> Result<Self, String> {
        if config.poll_interval.is_zero() {
            return Err("runtime poll interval must be positive".to_owned());
        }
        let state = AppStateStore::new();
        let mut initial_persistence_failures = 0;
        if let Some(cache) = config.quota_cache.as_ref() {
            match cache.load() {
                Ok(Some(restored)) => {
                    state.dispatch(StateEvent::CachedQuotaRestored {
                        summary: restored.summary,
                        last_success_at: restored.last_success_at,
                        source_cli_version: restored.source_cli_version,
                    });
                }
                Ok(None) => {}
                Err(_) => {
                    initial_persistence_failures = 1;
                    state.dispatch(StateEvent::PersistenceFailed);
                }
            }
        }
        let worker_state = state.clone();
        let (command_sender, command_receiver) = mpsc::channel();
        let thread = thread::Builder::new()
            .name("codex-quota-runtime".to_owned())
            .spawn(move || {
                runtime_worker(
                    config,
                    worker_state,
                    command_receiver,
                    initial_persistence_failures,
                )
            })
            .map_err(|error| format!("could not start quota runtime: {:?}", error.kind()))?;

        Ok(Self {
            state,
            command_sender,
            thread: Some(thread),
            shutdown_report: None,
        })
    }

    pub fn snapshot(&self) -> AppState {
        self.state.snapshot()
    }

    pub fn request_refresh(&self, reason: RefreshReason) -> Result<(), String> {
        if reason == RefreshReason::Manual {
            self.state
                .dispatch(StateEvent::RefreshStarted { at: unix_now() });
        }
        self.command_sender
            .send(RuntimeCommand::Refresh(reason))
            .map_err(|_| {
                if reason == RefreshReason::Manual {
                    self.state.dispatch(StateEvent::RefreshFailed {
                        at: unix_now(),
                        kind: FailureKind::Transport,
                    });
                }
                "quota runtime was not running".to_owned()
            })
    }

    pub fn set_refresh_mode(&self, mode: RefreshMode) -> Result<(), String> {
        self.command_sender
            .send(RuntimeCommand::SetRefreshMode(mode))
            .map_err(|_| "quota runtime was not running".to_owned())
    }

    pub fn shutdown(&mut self) -> Result<RuntimeReport, String> {
        if let Some(report) = self.shutdown_report.as_ref() {
            return Ok(report.clone());
        }
        let _ = self.command_sender.send(RuntimeCommand::Shutdown);
        let Some(thread) = self.thread.take() else {
            return Err("quota runtime thread was unavailable".to_owned());
        };
        let report = thread
            .join()
            .map_err(|_| "quota runtime thread panicked".to_owned())??;
        self.shutdown_report = Some(report.clone());
        Ok(report)
    }
}

impl Drop for QuotaRuntime {
    fn drop(&mut self) {
        let _ = self.shutdown();
    }
}

struct RuntimeCounters {
    refresh_successes: usize,
    refresh_failures: usize,
    rate_limit_notifications: usize,
    protocol_diagnostics: usize,
    persistence_failures: usize,
}

impl RuntimeCounters {
    fn new() -> Self {
        Self {
            refresh_successes: 0,
            refresh_failures: 0,
            rate_limit_notifications: 0,
            protocol_diagnostics: 0,
            persistence_failures: 0,
        }
    }
}

fn runtime_worker(
    config: RuntimeConfig,
    state: AppStateStore,
    command_receiver: Receiver<RuntimeCommand>,
    initial_persistence_failures: usize,
) -> Result<RuntimeReport, String> {
    let mut supervisor =
        AppServerSupervisor::start(config.launch.clone(), config.restart_policy.clone())?;
    let started_at = Instant::now();
    let mut coordinator = RefreshCoordinator::new(config.refresh_policy, config.refresh_mode);
    dispatch_stale_threshold(&state, &coordinator);
    let mut counters = RuntimeCounters::new();
    counters.persistence_failures = initial_persistence_failures;
    let mut manual_pending = false;

    let exit_reason = 'runtime: loop {
        match drain_waiting_commands(
            &command_receiver,
            &mut manual_pending,
            &mut coordinator,
            monotonic_secs(started_at),
        ) {
            CommandDisposition::Continue => {}
            CommandDisposition::Shutdown => break 'runtime RuntimeExitReason::ShutdownRequested,
        }

        let Some(event) = supervisor.next_event(config.poll_interval)? else {
            state.dispatch(StateEvent::Tick { now: unix_now() });
            continue;
        };

        match event {
            SupervisorEvent::Starting { generation } => {
                state.dispatch(StateEvent::ProcessStarting { generation });
            }
            SupervisorEvent::Started(connection) => {
                let generation = connection.generation();
                state.dispatch(StateEvent::ProcessHandshaking { generation });
                let client = JsonRpcClient::new(connection.server());
                let runtime_cli_version = match initialize_connection(&client) {
                    Ok(initialize) => {
                        let compatibility = evaluate_user_agent(
                            &initialize.user_agent,
                            &config.expected_schema_version,
                        );
                        let runtime_cli_version =
                            compatibility.runtime_version().map(str::to_owned);
                        state.dispatch(StateEvent::CompatibilityChecked(compatibility));
                        state.dispatch(StateEvent::ProcessReady {
                            generation,
                            at: unix_now(),
                        });
                        runtime_cli_version
                    }
                    Err(failure) if failure.recoverable_transport => {
                        state.dispatch(StateEvent::RefreshFailed {
                            at: unix_now(),
                            kind: failure.kind,
                        });
                        drop(client);
                        supervisor.request_restart()?;
                        continue;
                    }
                    Err(failure) => {
                        state.dispatch(StateEvent::RefreshFailed {
                            at: unix_now(),
                            kind: failure.kind,
                        });
                        state.dispatch(StateEvent::ProcessFailed);
                        break 'runtime RuntimeExitReason::FatalProtocol;
                    }
                };

                let reason = if generation == 0 {
                    RefreshReason::Startup
                } else {
                    RefreshReason::NetworkRestored
                };
                let mut actions = VecDeque::new();
                queue_decision(
                    &mut actions,
                    coordinator.request(reason, monotonic_secs(started_at))?,
                );
                if manual_pending {
                    manual_pending = false;
                    queue_decision(
                        &mut actions,
                        coordinator.request(RefreshReason::Manual, monotonic_secs(started_at))?,
                    );
                }

                match drive_connection(
                    &client,
                    &state,
                    &command_receiver,
                    &mut coordinator,
                    &mut actions,
                    &mut counters,
                    &config,
                    &runtime_cli_version,
                    started_at,
                )? {
                    ConnectionDisposition::Shutdown => {
                        drop(client);
                        break 'runtime RuntimeExitReason::ShutdownRequested;
                    }
                    ConnectionDisposition::Recover => {
                        drop(client);
                        supervisor.request_restart()?;
                    }
                    ConnectionDisposition::Fatal => {
                        drop(client);
                        state.dispatch(StateEvent::ProcessFailed);
                        break 'runtime RuntimeExitReason::FatalProtocol;
                    }
                }
            }
            SupervisorEvent::SpawnFailed { .. } => {}
            SupervisorEvent::ProcessExited { .. } => {
                state.dispatch(StateEvent::RefreshFailed {
                    at: unix_now(),
                    kind: FailureKind::Transport,
                });
            }
            SupervisorEvent::Backoff { attempt, .. } => {
                state.dispatch(StateEvent::ProcessBackoff { attempt });
            }
            SupervisorEvent::Exhausted { .. } => {
                state.dispatch(StateEvent::ProcessFailed);
                break 'runtime RuntimeExitReason::SupervisorExhausted;
            }
            SupervisorEvent::RestartRequested { .. } => {}
        }
    };

    let supervisor_report = supervisor.shutdown()?;
    if exit_reason == RuntimeExitReason::ShutdownRequested {
        state.dispatch(StateEvent::ProcessStopped);
    }
    Ok(RuntimeReport {
        exit_reason,
        supervisor: supervisor_report,
        refresh_successes: counters.refresh_successes,
        refresh_failures: counters.refresh_failures,
        rate_limit_notifications: counters.rate_limit_notifications,
        protocol_diagnostics: counters.protocol_diagnostics,
        persistence_failures: counters.persistence_failures,
    })
}

enum CommandDisposition {
    Continue,
    Shutdown,
}

fn drain_waiting_commands(
    receiver: &Receiver<RuntimeCommand>,
    manual_pending: &mut bool,
    coordinator: &mut RefreshCoordinator,
    now: u64,
) -> CommandDisposition {
    loop {
        match receiver.try_recv() {
            Ok(RuntimeCommand::Refresh(reason)) => {
                if crate::refresh::reason_allowed(coordinator.mode(), reason) {
                    *manual_pending = true;
                }
            }
            Ok(RuntimeCommand::SetRefreshMode(mode)) => {
                coordinator.set_mode(mode, now);
            }
            Ok(RuntimeCommand::Shutdown) | Err(TryRecvError::Disconnected) => {
                return CommandDisposition::Shutdown;
            }
            Err(TryRecvError::Empty) => return CommandDisposition::Continue,
        }
    }
}

enum ConnectionDisposition {
    Shutdown,
    Recover,
    Fatal,
}

#[allow(clippy::too_many_arguments)]
fn drive_connection(
    client: &JsonRpcClient,
    state: &AppStateStore,
    command_receiver: &Receiver<RuntimeCommand>,
    coordinator: &mut RefreshCoordinator,
    actions: &mut VecDeque<CoordinatorAction>,
    counters: &mut RuntimeCounters,
    config: &RuntimeConfig,
    runtime_cli_version: &Option<String>,
    started_at: Instant,
) -> Result<ConnectionDisposition, String> {
    loop {
        while let Some(action) = actions.pop_front() {
            match action {
                CoordinatorAction::Started(request) => match execute_refresh(
                    client,
                    state,
                    coordinator,
                    request,
                    counters,
                    config,
                    runtime_cli_version,
                    started_at,
                )? {
                    RefreshDisposition::Continue(mut follow_up) => actions.append(&mut follow_up),
                    RefreshDisposition::Recover => return Ok(ConnectionDisposition::Recover),
                },
                CoordinatorAction::TimedOut(request) => {
                    counters.refresh_failures += 1;
                    state.dispatch(StateEvent::RefreshFailed {
                        at: unix_now(),
                        kind: FailureKind::Timeout,
                    });
                    let _ = request;
                    dispatch_stale_threshold(state, coordinator);
                }
            }
        }

        loop {
            match command_receiver.try_recv() {
                Ok(RuntimeCommand::Refresh(reason)) => queue_decision(
                    actions,
                    coordinator.request(reason, monotonic_secs(started_at))?,
                ),
                Ok(RuntimeCommand::SetRefreshMode(mode)) => {
                    coordinator.set_mode(mode, monotonic_secs(started_at));
                    dispatch_stale_threshold(state, coordinator);
                }
                Ok(RuntimeCommand::Shutdown) | Err(TryRecvError::Disconnected) => {
                    return Ok(ConnectionDisposition::Shutdown);
                }
                Err(TryRecvError::Empty) => break,
            }
        }

        actions.extend(coordinator.tick(monotonic_secs(started_at))?);
        state.dispatch(StateEvent::Tick { now: unix_now() });
        if !actions.is_empty() {
            continue;
        }

        match client.receive_event(config.poll_interval) {
            Ok(Some(ClientEvent::Notification(notification)))
                if notification.method == RATE_LIMITS_UPDATED_METHOD =>
            {
                let Some(params) = notification.params else {
                    state.dispatch(StateEvent::RefreshFailed {
                        at: unix_now(),
                        kind: FailureKind::Protocol,
                    });
                    counters.protocol_diagnostics += 1;
                    continue;
                };
                let update: AccountRateLimitsUpdatedNotification =
                    match serde_json::from_value(params) {
                        Ok(update) => update,
                        Err(_) => {
                            state.dispatch(StateEvent::RefreshFailed {
                                at: unix_now(),
                                kind: FailureKind::Protocol,
                            });
                            counters.protocol_diagnostics += 1;
                            continue;
                        }
                    };
                counters.rate_limit_notifications += 1;
                state.dispatch(StateEvent::RateLimitsPatched {
                    patch: update.rate_limits,
                    received_at: unix_now(),
                });
                persist_snapshot(state, config, counters);
                queue_decision(
                    actions,
                    coordinator.request(
                        RefreshReason::RateLimitNotification,
                        monotonic_secs(started_at),
                    )?,
                );
            }
            Ok(Some(ClientEvent::Diagnostic(_))) => counters.protocol_diagnostics += 1,
            Ok(Some(ClientEvent::Notification(_))) | Ok(None) => {}
            Err(error) if rpc_failure(&error).recoverable_transport => {
                state.dispatch(StateEvent::RefreshFailed {
                    at: unix_now(),
                    kind: FailureKind::Transport,
                });
                return Ok(ConnectionDisposition::Recover);
            }
            Err(_) => return Ok(ConnectionDisposition::Fatal),
        }
    }
}

enum RefreshDisposition {
    Continue(VecDeque<CoordinatorAction>),
    Recover,
}

#[allow(clippy::too_many_arguments)]
fn execute_refresh(
    client: &JsonRpcClient,
    state: &AppStateStore,
    coordinator: &mut RefreshCoordinator,
    request: RefreshRequest,
    counters: &mut RuntimeCounters,
    config: &RuntimeConfig,
    runtime_cli_version: &Option<String>,
    started_at: Instant,
) -> Result<RefreshDisposition, String> {
    state.dispatch(StateEvent::RefreshStarted { at: unix_now() });
    let timeout = Duration::from_secs(config.refresh_policy.request_timeout_secs);
    let account = client.start_request(ACCOUNT_READ_METHOD, account_read_params(), timeout);
    let rate_limits =
        client.start_request(RATE_LIMITS_READ_METHOD, rate_limits_read_params(), timeout);

    let result = match (account, rate_limits) {
        (Ok(account), Ok(rate_limits)) => {
            let account = account.wait().map_err(|error| rpc_failure(&error));
            let rate_limits = rate_limits.wait().map_err(|error| rpc_failure(&error));
            match (account, rate_limits) {
                (Ok(account), Ok(rate_limits)) => {
                    let account: Result<AccountReadResponse, _> = parse_result(account);
                    let rate_limits: Result<RateLimitsReadResponse, _> = parse_result(rate_limits);
                    match (account, rate_limits) {
                        (Ok(account), Ok(rate_limits)) => {
                            let account = account_state(&account);
                            let is_chatgpt = matches!(account, AccountState::ChatGpt { .. });
                            state.dispatch(StateEvent::AccountUpdated(account.clone()));
                            if is_chatgpt {
                                state.dispatch(StateEvent::RateLimitsReplaced {
                                    response: rate_limits,
                                    received_at: unix_now(),
                                    source_cli_version: runtime_cli_version.clone(),
                                });
                                persist_snapshot(state, config, counters);
                            }
                            let succeeded = !is_chatgpt || state.snapshot().last_failure.is_none();
                            if succeeded {
                                Ok(())
                            } else {
                                Err(OperationFailure::new(FailureKind::IncompleteData, false))
                            }
                        }
                        _ => Err(OperationFailure::new(FailureKind::Protocol, false)),
                    }
                }
                (Err(error), _) | (_, Err(error)) => Err(error),
            }
        }
        (Err(error), _) | (_, Err(error)) => Err(rpc_failure(&error)),
    };

    let succeeded = result.is_ok();
    if succeeded {
        let remaining = state.snapshot().quota.as_ref().and_then(|quota| {
            quota
                .windows
                .iter()
                .filter(|window| window.percentage_valid)
                .map(|window| window.remaining_percent)
                .min()
        });
        coordinator.set_remaining_percent(remaining);
    }
    let (_, follow_up) = coordinator.complete(request.id, monotonic_secs(started_at), succeeded)?;
    dispatch_stale_threshold(state, coordinator);
    if succeeded {
        counters.refresh_successes += 1;
        return Ok(RefreshDisposition::Continue(follow_up.into()));
    }

    counters.refresh_failures += 1;
    let failure = result.expect_err("checked above");
    state.dispatch(StateEvent::RefreshFailed {
        at: unix_now(),
        kind: failure.kind,
    });
    if failure.recoverable_transport {
        Ok(RefreshDisposition::Recover)
    } else {
        Ok(RefreshDisposition::Continue(follow_up.into()))
    }
}

fn initialize_connection(client: &JsonRpcClient) -> Result<InitializeResponse, OperationFailure> {
    let result = client
        .request(INITIALIZE_METHOD, initialize_params(), INITIALIZE_TIMEOUT)
        .map_err(|error| rpc_failure(&error))?;
    let initialized = parse_result(result)?;
    client
        .notify(INITIALIZED_METHOD, None)
        .map_err(|error| rpc_failure(&error))?;
    Ok(initialized)
}

fn parse_result<T: DeserializeOwned>(result: Value) -> Result<T, OperationFailure> {
    serde_json::from_value(result).map_err(|_| OperationFailure::new(FailureKind::Protocol, false))
}

#[derive(Debug)]
struct OperationFailure {
    kind: FailureKind,
    recoverable_transport: bool,
}

impl OperationFailure {
    fn new(kind: FailureKind, recoverable_transport: bool) -> Self {
        Self {
            kind,
            recoverable_transport,
        }
    }
}

fn rpc_failure(error: &RpcClientError) -> OperationFailure {
    match error {
        RpcClientError::Transport { .. }
        | RpcClientError::TransportClosed
        | RpcClientError::ClientStopped => OperationFailure::new(FailureKind::Transport, true),
        RpcClientError::Timeout { .. } => OperationFailure::new(FailureKind::Timeout, false),
        RpcClientError::Remote { .. } => OperationFailure::new(FailureKind::Rpc, false),
        RpcClientError::Protocol { .. } | RpcClientError::RequestIdExhausted => {
            OperationFailure::new(FailureKind::Protocol, false)
        }
    }
}

fn queue_decision(actions: &mut VecDeque<CoordinatorAction>, decision: RequestDecision) {
    if let RequestDecision::Started(request) = decision {
        actions.push_back(CoordinatorAction::Started(request));
    }
}

fn persist_snapshot(state: &AppStateStore, config: &RuntimeConfig, counters: &mut RuntimeCounters) {
    let Some(cache) = config.quota_cache.as_ref() else {
        return;
    };
    if cache.save(&state.snapshot()).is_err() {
        counters.persistence_failures += 1;
        state.dispatch(StateEvent::PersistenceFailed);
    }
}

fn monotonic_secs(started_at: Instant) -> u64 {
    started_at.elapsed().as_secs()
}

fn dispatch_stale_threshold(state: &AppStateStore, coordinator: &RefreshCoordinator) {
    state.dispatch(StateEvent::StaleThresholdUpdated {
        seconds: coordinator.stale_after_secs().min(i64::MAX as u64) as i64,
        now: unix_now(),
    });
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs().min(i64::MAX as u64) as i64)
        .unwrap_or(0)
}
