use std::env;
use std::ffi::OsString;
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process;
use std::sync::{Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use codex_quota_tray::app_server::AppServerLaunch;
use codex_quota_tray::compatibility::VersionCompatibility;
use codex_quota_tray::persistence::QuotaCacheStore;
use codex_quota_tray::quota::{QuotaSummary, QuotaWindow, ResetCreditsState};
use codex_quota_tray::refresh::{RefreshPolicy, RefreshReason};
use codex_quota_tray::runtime::{QuotaRuntime, RuntimeConfig, RuntimeExitReason, RuntimeReport};
use codex_quota_tray::state::{AppState, DataState, ProcessState, StableDataState, WarningCode};
use codex_quota_tray::supervisor::RestartPolicy;
use serde_json::{Value, json};

const CHILD_FLAG: &str = "CODEX_QUOTA_TRAY_RUNTIME_CHILD";
const CHILD_MODE: &str = "CODEX_QUOTA_TRAY_RUNTIME_MODE";
const READ_COUNTER: &str = "CODEX_QUOTA_TRAY_RUNTIME_READ_COUNTER";
const GENERATION_COUNTER: &str = "CODEX_QUOTA_TRAY_RUNTIME_GENERATION_COUNTER";
const TEST_TIMEOUT: Duration = Duration::from_secs(12);
static RUNTIME_TEST_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn fake_runtime_server() {
    if env::var_os(CHILD_FLAG).is_none() {
        return;
    }

    let mode = env::var(CHILD_MODE).expect("fake runtime mode");
    let read_counter = PathBuf::from(env::var_os(READ_COUNTER).expect("read counter path"));
    let generation_counter =
        PathBuf::from(env::var_os(GENERATION_COUNTER).expect("generation counter path"));
    let generation = increment_counter(&generation_counter);
    serve_fake_app_server(&mode, &read_counter, generation);
}

#[test]
fn startup_projects_a_fresh_snapshot_and_shutdown_is_idempotent() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("steady");
    let mut runtime = QuotaRuntime::start(fixture.config()).unwrap();

    let snapshot = wait_for_state(&runtime, |state| {
        state.data == DataState::Fresh && used_percent(state) == Some(20)
    });
    assert!(matches!(
        snapshot.process,
        ProcessState::Ready { generation: 0 }
    ));
    assert_eq!(
        snapshot.quota.as_ref().unwrap().windows[0].display_name(),
        "Codex (7-day)"
    );

    let first = runtime.shutdown().unwrap();
    let second = runtime.shutdown().unwrap();
    assert_eq!(first, second);
    assert_clean_shutdown(&first);
}

#[test]
fn manual_refresh_burst_is_bounded_to_active_and_one_pending() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("steady");
    let mut runtime = QuotaRuntime::start(fixture.config()).unwrap();
    wait_for_state(&runtime, |state| state.data == DataState::Fresh);
    wait_for_counter(&fixture.read_counter, 1);

    for _ in 0..8 {
        runtime.request_refresh(RefreshReason::Manual).unwrap();
    }

    wait_for_counter(&fixture.read_counter, 2);
    thread::sleep(Duration::from_millis(1_250));
    let reads = read_counter(&fixture.read_counter);
    assert!((2..=3).contains(&reads));
    wait_for_state(&runtime, |state| {
        state.data == DataState::Fresh && used_percent(state) == Some(19 + reads as i64)
    });

    let report = runtime.shutdown().unwrap();
    assert_eq!(report.refresh_successes, reads as usize);
    assert_clean_shutdown(&report);
}

#[test]
fn sparse_notification_updates_state_and_schedules_a_full_read() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("notify");
    let mut runtime = QuotaRuntime::start(fixture.config()).unwrap();

    let patched = wait_for_state(&runtime, |state| used_percent(state) == Some(40));
    assert!(matches!(
        patched.data,
        DataState::Fresh
            | DataState::Refreshing {
                previous: StableDataState::Fresh
            }
    ));
    wait_for_counter(&fixture.read_counter, 2);
    let refreshed = wait_for_state(&runtime, |state| used_percent(state) == Some(41));
    assert_eq!(refreshed.data, DataState::Fresh);

    let report = runtime.shutdown().unwrap();
    assert_eq!(report.rate_limit_notifications, 1);
    assert_eq!(report.refresh_successes, 2);
    assert_clean_shutdown(&report);
}

#[test]
fn child_exit_preserves_then_replaces_quota_after_supervised_recovery() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("exit-once");
    let mut runtime = QuotaRuntime::start(fixture.config()).unwrap();

    wait_for_state(&runtime, |state| used_percent(state) == Some(20));
    let recovered = wait_for_state(&runtime, |state| {
        matches!(state.process, ProcessState::Ready { generation: 1 })
            && state.data == DataState::Fresh
            && used_percent(state) == Some(35)
    });
    assert_eq!(used_percent(&recovered), Some(35));

    let report = runtime.shutdown().unwrap();
    assert_eq!(report.supervisor.restart_count, 1);
    assert!(report.refresh_successes >= 2);
    assert_clean_shutdown(&report);
}

#[test]
fn schema_mismatch_is_explicit_but_read_only_quota_remains_best_effort() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("version-mismatch");
    let mut runtime = QuotaRuntime::start(fixture.config()).unwrap();

    let snapshot = wait_for_state(&runtime, |state| state.data == DataState::Fresh);
    assert_eq!(
        snapshot.compatibility,
        VersionCompatibility::Mismatch {
            schema_version: "0.137.0".to_owned(),
            runtime_version: "0.999.0".to_owned(),
        }
    );
    assert_eq!(snapshot.source_cli_version.as_deref(), Some("0.999.0"));
    assert_eq!(used_percent(&snapshot), Some(20));

    let report = runtime.shutdown().unwrap();
    assert_clean_shutdown(&report);
}

#[test]
fn runtime_restores_stale_cache_then_replaces_and_persists_live_data() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("slow-start");
    let cache = QuotaCacheStore::new(fixture.cache_path.clone());
    cache.save(&cached_state(88)).unwrap();
    let mut config = fixture.config();
    config.quota_cache = Some(cache.clone());
    let mut runtime = QuotaRuntime::start(config).unwrap();

    let restored = wait_for_state(&runtime, |state| used_percent(state) == Some(88));
    assert_eq!(restored.data, DataState::Stale);
    assert_eq!(restored.auth, codex_quota_tray::state::AuthState::Unknown);

    let live = wait_for_state(&runtime, |state| {
        state.data == DataState::Fresh && used_percent(state) == Some(20)
    });
    assert_eq!(live.source_cli_version.as_deref(), Some("0.137.0"));

    let report = runtime.shutdown().unwrap();
    assert_eq!(report.persistence_failures, 0);
    let persisted = cache.load().unwrap().unwrap();
    assert_eq!(persisted.summary.windows[0].used_percent, 20);
    assert_clean_shutdown(&report);
}

#[test]
fn corrupt_cache_warns_anonymously_without_blocking_live_refresh() {
    let _guard = runtime_test_guard();
    let fixture = RuntimeFixture::new("steady");
    fs::write(&fixture.cache_path, b"private content must not be reported").unwrap();
    let mut config = fixture.config();
    config.quota_cache = Some(QuotaCacheStore::new(fixture.cache_path.clone()));
    let mut runtime = QuotaRuntime::start(config).unwrap();

    let live = wait_for_state(&runtime, |state| {
        state.data == DataState::Fresh && used_percent(state) == Some(20)
    });
    assert!(live.warnings.contains(&WarningCode::PersistenceFailure));

    let report = runtime.shutdown().unwrap();
    assert_eq!(report.persistence_failures, 1);
    assert_clean_shutdown(&report);
}

fn runtime_test_guard() -> MutexGuard<'static, ()> {
    RUNTIME_TEST_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn serve_fake_app_server(mode: &str, read_counter: &Path, generation: u64) {
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout().lock();
    let mut account_request_id = None;
    let reader = BufReader::new(stdin.lock());

    for line in reader.lines() {
        let line = line.expect("request line");
        let Ok(request) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        match request.get("method").and_then(Value::as_str) {
            Some("initialize") => {
                if mode == "slow-start" {
                    thread::sleep(Duration::from_millis(250));
                }
                let version = if mode == "version-mismatch" {
                    "0.999.0"
                } else {
                    "0.137.0"
                };
                respond(
                    &mut stdout,
                    request_id(&request),
                    json!({
                        "userAgent": format!("codex_app_server_rs/{version}"),
                        "platformFamily": "windows",
                        "platformOs": "windows"
                    }),
                )
            }
            Some("initialized") => {}
            Some("account/read") => account_request_id = Some(request_id(&request)),
            Some("account/rateLimits/read") => {
                let read_number = append_read(read_counter);
                let used = match (mode, generation, read_number) {
                    ("steady", _, _) => 19 + read_number as i64,
                    ("exit-once", 1, _) => 20,
                    ("exit-once", _, _) => 35,
                    ("notify", _, 1) => 20,
                    ("notify", _, _) => 41,
                    _ => 20,
                };

                // Deliberately answer rate limits before account/read. The runtime must
                // correlate these concurrent responses by ID rather than by wire order.
                respond(&mut stdout, request_id(&request), rate_limits_result(used));
                respond(
                    &mut stdout,
                    account_request_id.take().expect("paired account/read"),
                    json!({
                        "account": {
                            "type": "chatgpt",
                            "email": "[REDACTED]",
                            "planType": "plus"
                        },
                        "requiresOpenaiAuth": true
                    }),
                );

                if mode == "notify" && read_number == 1 {
                    write_message(
                        &mut stdout,
                        &json!({
                            "method": "account/rateLimits/updated",
                            "params": {
                                "rateLimits": {
                                    "primary": { "usedPercent": 40 }
                                }
                            }
                        }),
                    );
                }
                stdout.flush().unwrap();

                if mode == "exit-once" && generation == 1 {
                    thread::sleep(Duration::from_millis(75));
                    process::exit(17);
                }
            }
            other => panic!("unexpected method: {other:?}"),
        }
        stdout.flush().unwrap();
    }
}

fn rate_limits_result(used_percent: i64) -> Value {
    json!({
        "rateLimits": {
            "credits": { "hasCredits": false, "unlimited": false },
            "limitId": "codex",
            "limitName": "Codex",
            "planType": "plus",
            "primary": {
                "resetsAt": 4_102_444_800_i64,
                "usedPercent": used_percent,
                "windowDurationMins": 10_080
            },
            "secondary": null
        },
        "rateLimitsByLimitId": null
    })
}

fn request_id(request: &Value) -> i64 {
    request
        .get("id")
        .and_then(Value::as_i64)
        .expect("numeric request ID")
}

fn respond(stdout: &mut impl Write, id: i64, result: Value) {
    write_message(stdout, &json!({ "id": id, "result": result }));
}

fn write_message(stdout: &mut impl Write, message: &Value) {
    serde_json::to_writer(&mut *stdout, message).unwrap();
    stdout.write_all(b"\n").unwrap();
}

struct RuntimeFixture {
    mode: &'static str,
    read_counter: PathBuf,
    generation_counter: PathBuf,
    cache_path: PathBuf,
}

impl RuntimeFixture {
    fn new(mode: &'static str) -> Self {
        Self {
            mode,
            read_counter: unique_path(mode, "reads"),
            generation_counter: unique_path(mode, "generations"),
            cache_path: unique_path(mode, "cache"),
        }
    }

    fn config(&self) -> RuntimeConfig {
        let env = vec![
            (OsString::from(CHILD_FLAG), OsString::from("1")),
            (OsString::from(CHILD_MODE), OsString::from(self.mode)),
            (
                OsString::from(READ_COUNTER),
                self.read_counter.as_os_str().to_owned(),
            ),
            (
                OsString::from(GENERATION_COUNTER),
                self.generation_counter.as_os_str().to_owned(),
            ),
        ];
        RuntimeConfig {
            launch: AppServerLaunch::custom(
                env::current_exe().unwrap().into_os_string(),
                vec![
                    OsString::from("--exact"),
                    OsString::from("fake_runtime_server"),
                    OsString::from("--nocapture"),
                ],
                env,
                Duration::from_secs(1),
            ),
            restart_policy: RestartPolicy::new(
                Duration::from_millis(10),
                Duration::from_millis(40),
                4,
                Duration::from_secs(2),
                Duration::from_secs(2),
                0,
                Duration::from_millis(5),
            )
            .unwrap(),
            refresh_policy: RefreshPolicy::new(1, 60, 5).unwrap(),
            poll_interval: Duration::from_millis(10),
            expected_schema_version: "0.137.0".to_owned(),
            quota_cache: None,
        }
    }
}

impl Drop for RuntimeFixture {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.read_counter);
        let _ = fs::remove_file(&self.generation_counter);
        let _ = fs::remove_file(&self.cache_path);
        let _ = fs::remove_file(self.cache_path.with_extension("json.bak"));
    }
}

fn cached_state(used_percent: i64) -> AppState {
    AppState {
        data: DataState::Fresh,
        quota: Some(QuotaSummary {
            windows: vec![QuotaWindow {
                limit_id: Some("must-not-persist".to_owned()),
                limit_name: Some("must-not-persist".to_owned()),
                source_slot: "primary",
                used_percent,
                remaining_percent: 100 - used_percent,
                window_duration_mins: Some(10_080),
                resets_at: Some(4_102_444_800),
            }],
            issues: Vec::new(),
            reset_credits: ResetCreditsState::UnavailableInSchema,
            rate_limit_reached: false,
        }),
        last_success_at: Some(1_700_000_000),
        source_cli_version: Some("0.137.0".to_owned()),
        ..AppState::default()
    }
}

fn wait_for_state(runtime: &QuotaRuntime, predicate: impl Fn(&AppState) -> bool) -> AppState {
    let deadline = Instant::now() + TEST_TIMEOUT;
    loop {
        let state = runtime.snapshot();
        if predicate(&state) {
            return state;
        }
        assert!(
            Instant::now() < deadline,
            "timed out waiting for runtime state; last state: {state:?}"
        );
        thread::sleep(Duration::from_millis(10));
    }
}

fn wait_for_counter(path: &Path, expected: u64) {
    let deadline = Instant::now() + TEST_TIMEOUT;
    while read_counter(path) < expected {
        assert!(
            Instant::now() < deadline,
            "timed out waiting for read count"
        );
        thread::sleep(Duration::from_millis(10));
    }
}

fn used_percent(state: &AppState) -> Option<i64> {
    state
        .quota
        .as_ref()
        .and_then(|quota| quota.windows.first())
        .map(|window| window.used_percent)
}

fn assert_clean_shutdown(report: &RuntimeReport) {
    assert_eq!(report.exit_reason, RuntimeExitReason::ShutdownRequested);
    assert_eq!(report.supervisor.forced_terminations, 0);
    assert!(!report.supervisor.exhausted);
}

fn append_read(path: &Path) -> u64 {
    let value = increment_counter(path);
    let mut file = OpenOptions::new().append(true).open(path).unwrap();
    writeln!(file).unwrap();
    value
}

fn increment_counter(path: &Path) -> u64 {
    let current = read_counter(path);
    let next = current + 1;
    fs::write(path, next.to_string()).unwrap();
    next
}

fn read_counter(path: &Path) -> u64 {
    fs::read_to_string(path)
        .ok()
        .and_then(|value| value.trim().parse().ok())
        .unwrap_or(0)
}

fn unique_path(name: &str, suffix: &str) -> PathBuf {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    env::temp_dir().join(format!(
        "codex-quota-runtime-{name}-{suffix}-{}-{unique}.counter",
        process::id()
    ))
}
