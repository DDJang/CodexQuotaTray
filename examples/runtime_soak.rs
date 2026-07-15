use std::ffi::OsString;
use std::thread;
use std::time::{Duration, Instant};

use codex_quota_tray::compatibility::VersionCompatibility;
use codex_quota_tray::runtime::{QuotaRuntime, RuntimeConfig, RuntimeExitReason};
use codex_quota_tray::state::{AppState, DataState};

#[derive(Debug)]
struct Options {
    codex_bin: Option<OsString>,
    duration: Duration,
    sample_interval: Duration,
}

fn main() {
    let exit_code = match parse_options() {
        Ok(options) => run(options),
        Err(message) => {
            eprintln!("Argument error: {message}");
            eprintln!(
                "Usage: cargo run --example runtime_soak -- [--seconds N] [--sample-seconds N] [--codex-bin PATH]"
            );
            1
        }
    };
    std::process::exit(exit_code);
}

fn parse_options() -> Result<Options, String> {
    let mut args = std::env::args_os().skip(1);
    let mut codex_bin = None;
    let mut seconds = 5 * 60;
    let mut sample_seconds = 30;

    while let Some(argument) = args.next() {
        match argument.to_string_lossy().as_ref() {
            "--codex-bin" => {
                codex_bin = Some(
                    args.next()
                        .ok_or_else(|| "--codex-bin requires a path or command".to_owned())?,
                );
            }
            "--seconds" => seconds = parse_positive(&mut args, "--seconds")?,
            "--sample-seconds" => sample_seconds = parse_positive(&mut args, "--sample-seconds")?,
            other => return Err(format!("unknown argument: {other}")),
        }
    }

    Ok(Options {
        codex_bin,
        duration: Duration::from_secs(seconds),
        sample_interval: Duration::from_secs(sample_seconds),
    })
}

fn parse_positive(args: &mut impl Iterator<Item = OsString>, name: &str) -> Result<u64, String> {
    let value = args
        .next()
        .ok_or_else(|| format!("{name} requires an integer"))?;
    let value = value
        .to_string_lossy()
        .parse::<u64>()
        .map_err(|_| format!("{name} must be a positive integer"))?;
    if value == 0 {
        return Err(format!("{name} must be positive"));
    }
    Ok(value)
}

fn run(options: Options) -> i32 {
    let mut runtime = match QuotaRuntime::start(RuntimeConfig::codex(options.codex_bin)) {
        Ok(runtime) => runtime,
        Err(message) => {
            eprintln!("Runtime startup failed: {message}");
            return 1;
        }
    };
    let started_at = Instant::now();
    let deadline = started_at + options.duration;
    let mut next_sample = started_at;
    let mut observed_fresh_quota = false;

    while Instant::now() < deadline {
        let now = Instant::now();
        if now >= next_sample {
            let state = runtime.snapshot();
            print_safe_sample(started_at.elapsed(), &state);
            observed_fresh_quota |= state.quota.is_some()
                && matches!(state.data, DataState::Fresh | DataState::Refreshing { .. });
            next_sample = now + options.sample_interval;
        }
        thread::sleep(Duration::from_millis(250));
    }

    let final_state = runtime.snapshot();
    print_safe_sample(started_at.elapsed(), &final_state);
    observed_fresh_quota |= final_state.quota.is_some();
    let report = match runtime.shutdown() {
        Ok(report) => report,
        Err(message) => {
            eprintln!("Runtime shutdown failed: {message}");
            return 1;
        }
    };
    println!(
        "soak complete: elapsed={}s refresh_successes={} refresh_failures={} notifications={} restarts={} forced_terminations={} protocol_diagnostics={}",
        started_at.elapsed().as_secs(),
        report.refresh_successes,
        report.refresh_failures,
        report.rate_limit_notifications,
        report.supervisor.restart_count,
        report.supervisor.forced_terminations,
        report.protocol_diagnostics,
    );

    if report.exit_reason != RuntimeExitReason::ShutdownRequested
        || report.supervisor.forced_terminations > 0
    {
        return 1;
    }
    if !observed_fresh_quota {
        eprintln!(
            "No usable quota snapshot was observed. Action: verify Codex login and inspect the normalized state samples above."
        );
        return 2;
    }
    0
}

fn print_safe_sample(elapsed: Duration, state: &AppState) {
    let compatibility = match &state.compatibility {
        VersionCompatibility::Unknown => "unknown".to_owned(),
        VersionCompatibility::Match {
            runtime_version, ..
        } => format!("match:{runtime_version}"),
        VersionCompatibility::Mismatch {
            schema_version,
            runtime_version,
        } => format!("mismatch:{runtime_version}!={schema_version}"),
        VersionCompatibility::Unreported { .. } => "unreported".to_owned(),
    };
    println!(
        "sample elapsed={}s process={:?} data={:?} windows={} compatibility={} last_success={} warnings={}",
        elapsed.as_secs(),
        state.process,
        state.data,
        state.quota.as_ref().map_or(0, |quota| quota.windows.len()),
        compatibility,
        state.last_success_at.is_some(),
        state.warnings.len(),
    );
}
