use std::collections::VecDeque;
use std::fmt;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, TryRecvError};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::app_server::{AppServer, AppServerLaunch, ProcessExit, ShutdownReport};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RestartPolicy {
    initial_delay: Duration,
    max_delay: Duration,
    max_restarts: usize,
    restart_window: Duration,
    stable_reset_after: Duration,
    jitter_percent: u8,
    poll_interval: Duration,
}

impl RestartPolicy {
    pub fn new(
        initial_delay: Duration,
        max_delay: Duration,
        max_restarts: usize,
        restart_window: Duration,
        stable_reset_after: Duration,
        jitter_percent: u8,
        poll_interval: Duration,
    ) -> Result<Self, String> {
        if initial_delay > max_delay {
            return Err("restart initial delay must not exceed maximum delay".to_owned());
        }
        if max_delay.is_zero() || restart_window.is_zero() || poll_interval.is_zero() {
            return Err("restart durations must be positive".to_owned());
        }
        if jitter_percent > 20 {
            return Err("restart jitter must be within 0..=20 percent".to_owned());
        }
        Ok(Self {
            initial_delay,
            max_delay,
            max_restarts,
            restart_window,
            stable_reset_after,
            jitter_percent,
            poll_interval,
        })
    }

    pub fn delay_for_attempt(&self, attempt: u32) -> Duration {
        let attempt = attempt.max(1);
        let shift = (attempt - 1).min(63);
        let base_millis = self
            .initial_delay
            .as_millis()
            .saturating_mul(1_u128 << shift)
            .min(self.max_delay.as_millis());
        let jitter_limit = base_millis.saturating_mul(u128::from(self.jitter_percent)) / 100;
        let jitter = if jitter_limit == 0 {
            0
        } else {
            u128::from(mix_attempt(attempt)) % (jitter_limit + 1)
        };
        let total = base_millis
            .saturating_add(jitter)
            .min(self.max_delay.as_millis())
            .min(u128::from(u64::MAX));
        Duration::from_millis(total as u64)
    }
}

impl Default for RestartPolicy {
    fn default() -> Self {
        Self {
            initial_delay: Duration::from_secs(1),
            max_delay: Duration::from_secs(30),
            max_restarts: 5,
            restart_window: Duration::from_secs(5 * 60),
            stable_reset_after: Duration::from_secs(5 * 60),
            jitter_percent: 20,
            poll_interval: Duration::from_millis(250),
        }
    }
}

#[derive(Clone)]
pub struct SupervisedConnection {
    generation: u64,
    server: Arc<Mutex<AppServer>>,
}

impl SupervisedConnection {
    pub fn generation(&self) -> u64 {
        self.generation
    }

    pub fn server(&self) -> Arc<Mutex<AppServer>> {
        Arc::clone(&self.server)
    }
}

impl fmt::Debug for SupervisedConnection {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("SupervisedConnection")
            .field("generation", &self.generation)
            .finish_non_exhaustive()
    }
}

#[derive(Debug, Clone)]
pub enum SupervisorEvent {
    Starting {
        generation: u64,
    },
    Started(SupervisedConnection),
    SpawnFailed {
        generation: u64,
    },
    ProcessExited {
        generation: u64,
        code: Option<i32>,
        success: bool,
    },
    Backoff {
        attempt: u32,
        delay: Duration,
    },
    Exhausted {
        restart_count: usize,
    },
    RestartRequested {
        generation: u64,
    },
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SupervisorReport {
    pub restart_count: usize,
    pub spawn_failures: usize,
    pub forced_terminations: usize,
    pub stderr_observed: bool,
    pub last_exit_code: Option<i32>,
    pub exhausted: bool,
}

enum SupervisorControl {
    Shutdown,
    Restart,
}

pub struct AppServerSupervisor {
    control_sender: Sender<SupervisorControl>,
    event_receiver: Receiver<SupervisorEvent>,
    thread: Option<JoinHandle<Result<SupervisorReport, String>>>,
    shutdown_report: Option<SupervisorReport>,
}

impl AppServerSupervisor {
    pub fn start(launch: AppServerLaunch, policy: RestartPolicy) -> Result<Self, String> {
        let (control_sender, control_receiver) = mpsc::channel();
        let (event_sender, event_receiver) = mpsc::channel();
        let thread = thread::Builder::new()
            .name("codex-app-server-supervisor".to_owned())
            .spawn(move || supervisor_loop(launch, policy, control_receiver, event_sender))
            .map_err(|error| format!("could not start supervisor thread: {:?}", error.kind()))?;

        Ok(Self {
            control_sender,
            event_receiver,
            thread: Some(thread),
            shutdown_report: None,
        })
    }

    pub fn next_event(&self, timeout: Duration) -> Result<Option<SupervisorEvent>, String> {
        match self.event_receiver.recv_timeout(timeout) {
            Ok(event) => Ok(Some(event)),
            Err(RecvTimeoutError::Timeout) => Ok(None),
            Err(RecvTimeoutError::Disconnected) => {
                Err("App Server supervisor event stream closed".to_owned())
            }
        }
    }

    pub fn request_restart(&self) -> Result<(), String> {
        self.control_sender
            .send(SupervisorControl::Restart)
            .map_err(|_| "App Server supervisor was not running".to_owned())
    }

    pub fn shutdown(&mut self) -> Result<SupervisorReport, String> {
        if let Some(report) = self.shutdown_report.as_ref() {
            return Ok(report.clone());
        }

        let _ = self.control_sender.send(SupervisorControl::Shutdown);
        let Some(thread) = self.thread.take() else {
            return Err("App Server supervisor thread was unavailable".to_owned());
        };
        let report = thread
            .join()
            .map_err(|_| "App Server supervisor thread panicked".to_owned())??;
        self.shutdown_report = Some(report.clone());
        Ok(report)
    }
}

impl Drop for AppServerSupervisor {
    fn drop(&mut self) {
        let _ = self.shutdown();
    }
}

fn supervisor_loop(
    launch: AppServerLaunch,
    policy: RestartPolicy,
    control_receiver: Receiver<SupervisorControl>,
    event_sender: Sender<SupervisorEvent>,
) -> Result<SupervisorReport, String> {
    let mut report = SupervisorReport::default();
    let mut generation = 0_u64;
    let mut consecutive_failures = 0_u32;
    let mut restart_times = VecDeque::new();

    loop {
        if shutdown_requested(&control_receiver) {
            return Ok(report);
        }

        send_event(&event_sender, SupervisorEvent::Starting { generation });
        match AppServer::spawn_launch(&launch) {
            Ok(server) => {
                let server = Arc::new(Mutex::new(server));
                send_event(
                    &event_sender,
                    SupervisorEvent::Started(SupervisedConnection {
                        generation,
                        server: Arc::clone(&server),
                    }),
                );

                let started_at = Instant::now();
                match monitor_process(&server, policy.poll_interval, &control_receiver)? {
                    MonitorOutcome::Shutdown => {
                        let shutdown = shutdown_server(&server)?;
                        merge_shutdown_report(&mut report, &shutdown);
                        return Ok(report);
                    }
                    MonitorOutcome::Exited(exit) => {
                        let shutdown = shutdown_server(&server)?;
                        merge_shutdown_report(&mut report, &shutdown);
                        report.last_exit_code = exit.code;
                        send_event(
                            &event_sender,
                            SupervisorEvent::ProcessExited {
                                generation,
                                code: exit.code,
                                success: exit.success,
                            },
                        );
                        if started_at.elapsed() >= policy.stable_reset_after {
                            consecutive_failures = 0;
                        }
                    }
                    MonitorOutcome::RestartRequested => {
                        let shutdown = shutdown_server(&server)?;
                        merge_shutdown_report(&mut report, &shutdown);
                        send_event(
                            &event_sender,
                            SupervisorEvent::RestartRequested { generation },
                        );
                    }
                }
            }
            Err(_) => {
                report.spawn_failures += 1;
                send_event(&event_sender, SupervisorEvent::SpawnFailed { generation });
            }
        }

        let now = Instant::now();
        while restart_times
            .front()
            .is_some_and(|started| now.duration_since(*started) >= policy.restart_window)
        {
            restart_times.pop_front();
        }
        if restart_times.len() >= policy.max_restarts {
            report.exhausted = true;
            send_event(
                &event_sender,
                SupervisorEvent::Exhausted {
                    restart_count: report.restart_count,
                },
            );
            return Ok(report);
        }

        consecutive_failures = consecutive_failures.saturating_add(1);
        let delay = policy.delay_for_attempt(consecutive_failures);
        send_event(
            &event_sender,
            SupervisorEvent::Backoff {
                attempt: consecutive_failures,
                delay,
            },
        );
        if wait_backoff(&control_receiver, delay) {
            return Ok(report);
        }

        restart_times.push_back(Instant::now());
        report.restart_count += 1;
        generation = generation.saturating_add(1);
    }
}

enum MonitorOutcome {
    Shutdown,
    Exited(ProcessExit),
    RestartRequested,
}

fn monitor_process(
    server: &Arc<Mutex<AppServer>>,
    poll_interval: Duration,
    control_receiver: &Receiver<SupervisorControl>,
) -> Result<MonitorOutcome, String> {
    loop {
        if let Some(exit) = lock_unpoisoned(server).try_exit()? {
            return Ok(MonitorOutcome::Exited(exit));
        }

        match control_receiver.recv_timeout(poll_interval) {
            Ok(SupervisorControl::Shutdown) | Err(RecvTimeoutError::Disconnected) => {
                return Ok(MonitorOutcome::Shutdown);
            }
            Ok(SupervisorControl::Restart) => return Ok(MonitorOutcome::RestartRequested),
            Err(RecvTimeoutError::Timeout) => {}
        }
    }
}

fn shutdown_server(server: &Arc<Mutex<AppServer>>) -> Result<ShutdownReport, String> {
    lock_unpoisoned(server).shutdown()
}

fn merge_shutdown_report(report: &mut SupervisorReport, shutdown: &ShutdownReport) {
    report.forced_terminations += usize::from(shutdown.forced);
    report.stderr_observed |= shutdown.stderr_observed;
    report.last_exit_code = shutdown.exit_code;
}

fn shutdown_requested(receiver: &Receiver<SupervisorControl>) -> bool {
    match receiver.try_recv() {
        Ok(SupervisorControl::Shutdown) | Err(TryRecvError::Disconnected) => true,
        Ok(SupervisorControl::Restart) => false,
        Err(TryRecvError::Empty) => false,
    }
}

fn wait_backoff(receiver: &Receiver<SupervisorControl>, delay: Duration) -> bool {
    let started = Instant::now();
    let deadline = started.checked_add(delay).unwrap_or(started);
    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return false;
        }
        match receiver.recv_timeout(remaining) {
            Ok(SupervisorControl::Shutdown) | Err(RecvTimeoutError::Disconnected) => return true,
            Ok(SupervisorControl::Restart) => {}
            Err(RecvTimeoutError::Timeout) => return false,
        }
    }
}

fn send_event(sender: &Sender<SupervisorEvent>, event: SupervisorEvent) {
    let _ = sender.send(event);
}

fn lock_unpoisoned<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn mix_attempt(attempt: u32) -> u64 {
    let mut value = u64::from(attempt).wrapping_add(0x9E37_79B9_7F4A_7C15);
    value = (value ^ (value >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    value ^ (value >> 31)
}

#[cfg(test)]
mod tests {
    use super::RestartPolicy;
    use std::time::Duration;

    #[test]
    fn backoff_is_exponential_and_capped() {
        let policy = RestartPolicy::new(
            Duration::from_millis(10),
            Duration::from_millis(50),
            5,
            Duration::from_secs(1),
            Duration::from_secs(1),
            0,
            Duration::from_millis(5),
        )
        .unwrap();

        assert_eq!(policy.delay_for_attempt(1), Duration::from_millis(10));
        assert_eq!(policy.delay_for_attempt(2), Duration::from_millis(20));
        assert_eq!(policy.delay_for_attempt(3), Duration::from_millis(40));
        assert_eq!(policy.delay_for_attempt(4), Duration::from_millis(50));
        assert_eq!(policy.delay_for_attempt(40), Duration::from_millis(50));
    }

    #[test]
    fn invalid_policy_is_rejected() {
        let error = RestartPolicy::new(
            Duration::from_secs(2),
            Duration::from_secs(1),
            5,
            Duration::from_secs(1),
            Duration::from_secs(1),
            0,
            Duration::from_millis(5),
        )
        .unwrap_err();
        assert!(error.contains("initial delay"));
    }
}
