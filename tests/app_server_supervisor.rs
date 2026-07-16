use std::env;
use std::ffi::OsString;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process;
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use codex_quota_tray::app_server::{AppServer, AppServerLaunch};
use codex_quota_tray::supervisor::{AppServerSupervisor, RestartPolicy, SupervisorEvent};

const CHILD_FLAG: &str = "CODEX_QUOTA_TRAY_FAKE_CHILD";
const CHILD_MODE: &str = "CODEX_QUOTA_TRAY_FAKE_MODE";
const CHILD_COUNTER: &str = "CODEX_QUOTA_TRAY_FAKE_COUNTER";
const CHILD_FAILURES: &str = "CODEX_QUOTA_TRAY_FAKE_FAILURES";
const CHILD_PID_FILE: &str = "CODEX_QUOTA_TRAY_FAKE_PID_FILE";
const TEST_TIMEOUT: Duration = Duration::from_secs(5);

#[test]
#[allow(clippy::zombie_processes)] // The process-tree fixture intentionally leaves a descendant for the job to reap.
fn fake_app_server_child() {
    if env::var_os(CHILD_FLAG).is_none() {
        return;
    }

    match env::var(CHILD_MODE).as_deref() {
        Ok("graceful") => wait_for_stdin_eof(),
        Ok("stderr-flood") => {
            let mut stderr = std::io::stderr().lock();
            let chunk = [b'x'; 8192];
            for _ in 0..256 {
                stderr.write_all(&chunk).unwrap();
            }
            stderr.flush().unwrap();
            drop(stderr);
            wait_for_stdin_eof();
        }
        Ok("ignore-stdin") => loop {
            thread::sleep(Duration::from_millis(100));
        },
        Ok("fail-then-wait") => {
            let attempt = increment_attempt(&PathBuf::from(
                env::var_os(CHILD_COUNTER).expect("counter path"),
            ));
            let failures = env::var(CHILD_FAILURES)
                .expect("failure count")
                .parse::<u32>()
                .expect("numeric failure count");
            if attempt <= failures {
                process::exit(17);
            }
            wait_for_stdin_eof();
        }
        Ok("spawn-descendant") => {
            thread::sleep(Duration::from_millis(100));
            let mut descendant = process::Command::new(env::current_exe().unwrap());
            descendant
                .args(["--exact", "fake_app_server_child", "--nocapture"])
                .env(CHILD_FLAG, "1")
                .env(CHILD_MODE, "descendant")
                .stdin(process::Stdio::null());
            let descendant = descendant.spawn().unwrap();
            fs::write(
                env::var_os(CHILD_PID_FILE).expect("pid file path"),
                descendant.id().to_string(),
            )
            .unwrap();
            wait_for_stdin_eof();
        }
        Ok("descendant") => loop {
            thread::sleep(Duration::from_millis(100));
        },
        other => panic!("unknown fake child mode: {other:?}"),
    }
}

#[test]
fn app_server_shutdown_is_idempotent() {
    let launch = fake_launch("graceful", None, 0, Duration::from_secs(1));
    let mut server = AppServer::spawn_launch(&launch).unwrap();
    let first = server.shutdown().unwrap();
    let second = server.shutdown().unwrap();

    assert_eq!(first, second);
    assert!(!first.forced);
    assert_eq!(first.exit_code, Some(0));
}

#[test]
fn stderr_is_drained_without_persisting_raw_text() {
    let launch = fake_launch("stderr-flood", None, 0, Duration::from_secs(2));
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(1, Duration::from_millis(10))).unwrap();
    wait_for_generation(&supervisor, 0);

    let first = supervisor.shutdown().unwrap();
    let second = supervisor.shutdown().unwrap();

    assert_eq!(first, second);
    assert!(first.stderr_observed);
    assert_eq!(first.forced_terminations, 0);
    assert!(!format!("{first:?}").contains("xxxxxxxx"));
}

#[test]
fn nonzero_exit_restarts_until_a_generation_stays_running() {
    let counter = unique_counter_path("recover");
    let launch = fake_launch("fail-then-wait", Some(&counter), 2, Duration::from_secs(1));
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(3, Duration::from_millis(10))).unwrap();

    let deadline = Instant::now() + TEST_TIMEOUT;
    let mut exit_codes = Vec::new();
    let mut delays = Vec::new();
    loop {
        let event = next_until(&supervisor, deadline);
        match event {
            SupervisorEvent::ProcessExited { code, .. } => exit_codes.push(code),
            SupervisorEvent::Backoff { delay, .. } => delays.push(delay),
            SupervisorEvent::Started(connection) if connection.generation() == 2 => break,
            _ => {}
        }
    }

    let report = supervisor.shutdown().unwrap();
    assert_eq!(exit_codes, [Some(17), Some(17)]);
    assert_eq!(
        delays,
        [Duration::from_millis(10), Duration::from_millis(20)]
    );
    assert_eq!(report.restart_count, 2);
    assert_eq!(report.forced_terminations, 0);
    remove_counter(&counter);
}

#[test]
fn explicit_recovery_request_replaces_the_connection_generation() {
    let launch = fake_launch("graceful", None, 0, Duration::from_secs(1));
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(2, Duration::from_millis(5))).unwrap();
    wait_for_generation(&supervisor, 0);

    supervisor.request_restart().unwrap();
    let deadline = Instant::now() + TEST_TIMEOUT;
    let mut saw_restart_event = false;
    loop {
        match next_until(&supervisor, deadline) {
            SupervisorEvent::RestartRequested { generation: 0 } => {
                saw_restart_event = true;
            }
            SupervisorEvent::Started(connection) if connection.generation() == 1 => break,
            _ => {}
        }
    }

    let report = supervisor.shutdown().unwrap();
    assert!(saw_restart_event);
    assert_eq!(report.restart_count, 1);
    assert_eq!(report.forced_terminations, 0);
}

#[test]
fn restart_budget_is_bounded() {
    let counter = unique_counter_path("exhaust");
    let launch = fake_launch(
        "fail-then-wait",
        Some(&counter),
        u32::MAX,
        Duration::from_secs(1),
    );
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(2, Duration::from_millis(5))).unwrap();

    let deadline = Instant::now() + TEST_TIMEOUT;
    let mut exits = 0;
    loop {
        match next_until(&supervisor, deadline) {
            SupervisorEvent::ProcessExited { code: Some(17), .. } => exits += 1,
            SupervisorEvent::Exhausted { restart_count } => {
                assert_eq!(restart_count, 2);
                break;
            }
            _ => {}
        }
    }

    let report = supervisor.shutdown().unwrap();
    assert_eq!(exits, 3);
    assert_eq!(report.restart_count, 2);
    assert!(report.exhausted);
    remove_counter(&counter);
}

#[test]
fn spawn_failures_use_the_same_bounded_budget() {
    let missing = env::temp_dir().join(format!(
        "codex-quota-tray-missing-{}-{}.exe",
        process::id(),
        unique_suffix()
    ));
    let launch = AppServerLaunch::custom(
        missing.into_os_string(),
        Vec::new(),
        Vec::new(),
        Duration::from_millis(100),
    );
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(1, Duration::from_millis(5))).unwrap();

    let deadline = Instant::now() + TEST_TIMEOUT;
    loop {
        if let SupervisorEvent::Exhausted { restart_count } = next_until(&supervisor, deadline) {
            assert_eq!(restart_count, 1);
            break;
        }
    }

    let report = supervisor.shutdown().unwrap();
    assert_eq!(report.spawn_failures, 2);
    assert_eq!(report.restart_count, 1);
    assert!(report.exhausted);
}

#[test]
fn unresponsive_child_is_forced_once_and_shutdown_remains_idempotent() {
    let launch = fake_launch("ignore-stdin", None, 0, Duration::from_millis(100));
    let mut supervisor =
        AppServerSupervisor::start(launch, test_policy(1, Duration::from_millis(5))).unwrap();
    wait_for_generation(&supervisor, 0);

    let first = supervisor.shutdown().unwrap();
    let second = supervisor.shutdown().unwrap();

    assert_eq!(first, second);
    assert_eq!(first.forced_terminations, 1);
}

#[cfg(windows)]
#[test]
fn shutdown_reaps_descendants_that_keep_transport_pipes_open() {
    let pid_file = unique_counter_path("descendant-pid");
    let launch = AppServerLaunch::custom(
        env::current_exe().unwrap().into_os_string(),
        vec![
            OsString::from("--exact"),
            OsString::from("fake_app_server_child"),
            OsString::from("--nocapture"),
        ],
        vec![
            (OsString::from(CHILD_FLAG), OsString::from("1")),
            (
                OsString::from(CHILD_MODE),
                OsString::from("spawn-descendant"),
            ),
            (
                OsString::from(CHILD_PID_FILE),
                pid_file.as_os_str().to_owned(),
            ),
        ],
        Duration::from_secs(1),
    );
    let mut server = AppServer::spawn_launch(&launch).unwrap();
    let deadline = Instant::now() + TEST_TIMEOUT;
    while !pid_file.exists() {
        assert!(Instant::now() < deadline, "descendant did not start");
        thread::sleep(Duration::from_millis(10));
    }
    let descendant_pid = fs::read_to_string(&pid_file)
        .unwrap()
        .parse::<u32>()
        .unwrap();

    let started = Instant::now();
    let report = server.shutdown().unwrap();

    assert!(!report.forced);
    assert!(started.elapsed() < Duration::from_secs(3));
    assert!(process_has_exited(descendant_pid));
    remove_counter(&pid_file);
}

#[cfg(windows)]
fn process_has_exited(pid: u32) -> bool {
    use windows::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
    use windows::Win32::System::Threading::{
        OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    };

    // SAFETY: The handle is used only for synchronization and closed before returning.
    let Ok(handle) = (unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, pid) }) else {
        return true;
    };
    // SAFETY: `handle` is a valid process handle returned above.
    let result = unsafe { WaitForSingleObject(handle, 2_000) } == WAIT_OBJECT_0;
    // SAFETY: This function owns the process handle.
    let _ = unsafe { CloseHandle(handle) };
    result
}

fn fake_launch(
    mode: &str,
    counter: Option<&Path>,
    failures: u32,
    shutdown_timeout: Duration,
) -> AppServerLaunch {
    let mut child_env = vec![
        (OsString::from(CHILD_FLAG), OsString::from("1")),
        (OsString::from(CHILD_MODE), OsString::from(mode)),
        (
            OsString::from(CHILD_FAILURES),
            OsString::from(failures.to_string()),
        ),
    ];
    if let Some(counter) = counter {
        child_env.push((
            OsString::from(CHILD_COUNTER),
            counter.as_os_str().to_owned(),
        ));
    }

    AppServerLaunch::custom(
        env::current_exe().unwrap().into_os_string(),
        vec![
            OsString::from("--exact"),
            OsString::from("fake_app_server_child"),
            OsString::from("--nocapture"),
        ],
        child_env,
        shutdown_timeout,
    )
}

fn test_policy(max_restarts: usize, initial_delay: Duration) -> RestartPolicy {
    RestartPolicy::new(
        initial_delay,
        Duration::from_millis(40),
        max_restarts,
        Duration::from_secs(1),
        Duration::from_secs(1),
        0,
        Duration::from_millis(5),
    )
    .unwrap()
}

fn wait_for_generation(supervisor: &AppServerSupervisor, generation: u64) {
    let deadline = Instant::now() + TEST_TIMEOUT;
    loop {
        if let SupervisorEvent::Started(connection) = next_until(supervisor, deadline)
            && connection.generation() == generation
        {
            return;
        }
    }
}

fn next_until(supervisor: &AppServerSupervisor, deadline: Instant) -> SupervisorEvent {
    let now = Instant::now();
    assert!(now < deadline, "timed out waiting for supervisor event");
    supervisor
        .next_event(deadline.saturating_duration_since(now))
        .expect("supervisor event stream")
        .expect("supervisor event before deadline")
}

fn wait_for_stdin_eof() {
    let mut input = Vec::new();
    std::io::stdin().read_to_end(&mut input).unwrap();
}

fn increment_attempt(path: &Path) -> u32 {
    let current = fs::read_to_string(path)
        .ok()
        .and_then(|value| value.parse::<u32>().ok())
        .unwrap_or(0);
    let next = current + 1;
    fs::write(path, next.to_string()).unwrap();
    next
}

fn unique_counter_path(name: &str) -> PathBuf {
    env::temp_dir().join(format!(
        "codex-quota-tray-{name}-{}-{}.counter",
        process::id(),
        unique_suffix()
    ))
}

fn unique_suffix() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos()
}

fn remove_counter(path: &Path) {
    let _ = fs::remove_file(path);
}
