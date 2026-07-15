use std::ffi::OsString;
use std::time::{Duration, Instant};

use codex_quota_tray::app_server::{AppServer, TransportEvent};
use codex_quota_tray::protocol::{
    ACCOUNT_READ_ID, AccountRateLimitsUpdatedNotification, AccountReadResponse, INITIALIZE_ID,
    IncomingMessage, InitializeResponse, RATE_LIMITS_READ_ID, RATE_LIMITS_UPDATED_METHOD,
    RateLimitsReadResponse, account_read_request, initialize_request, initialized_notification,
    rate_limits_read_request,
};
use codex_quota_tray::quota::{
    AccountState, QuotaSummary, account_state, format_reset_time, summarize_rate_limits,
};

const SCHEMA_CODEX_VERSION: &str = "0.137.0";
const INITIALIZE_TIMEOUT: Duration = Duration::from_secs(10);
const READ_TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Debug)]
struct Options {
    codex_bin: Option<OsString>,
    watch_seconds: u64,
}

enum ParseOutcome {
    Run(Options),
    Help,
}

fn main() {
    let exit_code = match parse_options() {
        Ok(ParseOutcome::Help) => {
            print_help();
            0
        }
        Ok(ParseOutcome::Run(options)) => run(options),
        Err(message) => {
            eprintln!("Argument error: {message}");
            eprintln!("Run with --help for usage.");
            1
        }
    };
    std::process::exit(exit_code);
}

fn parse_options() -> Result<ParseOutcome, String> {
    let mut args = std::env::args_os().skip(1);
    let mut codex_bin = None;
    let mut watch_seconds = 10_u64;

    while let Some(argument) = args.next() {
        match argument.to_string_lossy().as_ref() {
            "--help" | "-h" => return Ok(ParseOutcome::Help),
            "--codex-bin" => {
                codex_bin = Some(
                    args.next()
                        .ok_or_else(|| "--codex-bin requires a path or command".to_owned())?,
                );
            }
            "--watch-seconds" => {
                let value = args
                    .next()
                    .ok_or_else(|| "--watch-seconds requires an integer".to_owned())?;
                watch_seconds = value
                    .to_string_lossy()
                    .parse::<u64>()
                    .map_err(|_| "--watch-seconds must be a non-negative integer".to_owned())?;
            }
            other => return Err(format!("unknown argument: {other}")),
        }
    }

    Ok(ParseOutcome::Run(Options {
        codex_bin,
        watch_seconds,
    }))
}

fn print_help() {
    println!("Read Codex quota information through codex app-server.");
    println!();
    println!("Usage: codex-quota-tray [--codex-bin PATH] [--watch-seconds N]");
    println!();
    println!("  --codex-bin PATH    Override Codex executable or command discovery");
    println!(
        "  --watch-seconds N   Listen for rate-limit updates after the initial read (default: 10)"
    );
}

fn run(options: Options) -> i32 {
    let mut server = match AppServer::spawn(options.codex_bin) {
        Ok(server) => server,
        Err(message) => {
            eprintln!("Codex App Server unavailable: {message}");
            eprintln!("Action: install Codex CLI or pass its executable with --codex-bin.");
            return 1;
        }
    };

    let session_code = match run_session(&mut server, options.watch_seconds) {
        Ok(code) => code,
        Err(message) => {
            eprintln!("P0 spike failed: {message}");
            1
        }
    };

    match server.shutdown() {
        Ok(report) if report.forced => {
            eprintln!("App Server did not exit after stdin closed and had to be terminated.");
            1
        }
        Ok(report) => {
            if report.stderr_observed {
                eprintln!("Note: App Server wrote diagnostics to stderr; raw text was suppressed.");
            }
            if report.exit_code != Some(0) {
                eprintln!("App Server exited with status {:?}.", report.exit_code);
                1
            } else {
                session_code
            }
        }
        Err(message) => {
            eprintln!("Could not cleanly stop App Server: {message}");
            1
        }
    }
}

fn run_session(server: &mut AppServer, watch_seconds: u64) -> Result<i32, String> {
    server.send(&initialize_request())?;
    let initialize_message = wait_for_response(server, INITIALIZE_ID, INITIALIZE_TIMEOUT)?;
    reject_rpc_error(&initialize_message, "initialize")?;
    let initialize: InitializeResponse = parse_result(initialize_message, "initialize")?;

    println!("Codex App Server: {}", initialize.user_agent);
    println!(
        "Platform: {}/{}; generated schema: codex-cli {SCHEMA_CODEX_VERSION}",
        initialize.platform_family, initialize.platform_os
    );
    if !initialize.user_agent.contains(SCHEMA_CODEX_VERSION) {
        eprintln!(
            "Warning: running App Server does not advertise schema version {SCHEMA_CODEX_VERSION}; regenerate schemas before relying on new fields."
        );
    }

    server.send(&initialized_notification())?;
    server.send(&account_read_request())?;
    server.send(&rate_limits_read_request())?;

    let deadline = Instant::now() + READ_TIMEOUT;
    let mut account_response = None;
    let mut rate_limits_response = None;
    while account_response.is_none() || rate_limits_response.is_none() {
        let event = receive_until(server, deadline)?;
        let TransportEvent::Message(message) = event else {
            if let TransportEvent::MalformedLine(detail) = event {
                return Err(format!("App Server emitted malformed JSONL: {detail}"));
            }
            unreachable!();
        };

        if message.has_id(ACCOUNT_READ_ID) {
            reject_rpc_error(&message, "account/read")?;
            account_response = Some(parse_result(message, "account/read")?);
        } else if message.has_id(RATE_LIMITS_READ_ID) {
            reject_rpc_error(&message, "account/rateLimits/read")?;
            rate_limits_response = Some(parse_result(message, "account/rateLimits/read")?);
        }
    }

    let account_response: AccountReadResponse = account_response.expect("checked above");
    let account = account_state(&account_response);
    if let Some(code) = print_account_or_explain(&account) {
        return Ok(code);
    }

    let mut rate_limits_response: RateLimitsReadResponse =
        rate_limits_response.expect("checked above");
    let summary = summarize_rate_limits(&rate_limits_response);
    if summary.windows.is_empty() {
        eprintln!("No complete quota window was returned by this account.");
        print_issues(&summary);
        eprintln!(
            "Action: verify the same account in Codex /status or /usage and regenerate schemas after a CLI upgrade."
        );
        return Ok(2);
    }
    print_summary("Rate limits", &summary);
    println!(
        "Reset credits: unavailable (codex-cli {SCHEMA_CODEX_VERSION} does not expose a reset-credit count)"
    );

    if watch_seconds == 0 {
        println!("Update listener disabled by --watch-seconds 0.");
        return Ok(0);
    }

    println!("Listening for account/rateLimits/updated for {watch_seconds} seconds...");
    let watch_deadline = Instant::now() + Duration::from_secs(watch_seconds);
    while Instant::now() < watch_deadline {
        let remaining = watch_deadline.saturating_duration_since(Instant::now());
        let timeout = remaining.min(Duration::from_secs(1));
        let Some(event) = server.receive(timeout)? else {
            continue;
        };

        match event {
            TransportEvent::MalformedLine(detail) => {
                return Err(format!("App Server emitted malformed JSONL: {detail}"));
            }
            TransportEvent::Message(message)
                if message.method.as_deref() == Some(RATE_LIMITS_UPDATED_METHOD) =>
            {
                let params = message.params.ok_or_else(|| {
                    "account/rateLimits/updated omitted required params".to_owned()
                })?;
                let notification: AccountRateLimitsUpdatedNotification =
                    serde_json::from_value(params).map_err(|error| {
                        format!(
                            "account/rateLimits/updated shape mismatch at line {}, column {}",
                            error.line(),
                            error.column()
                        )
                    })?;
                rate_limits_response.merge_sparse_notification(notification.rate_limits);
                let updated = summarize_rate_limits(&rate_limits_response);
                print_summary("Rate limits updated", &updated);
            }
            TransportEvent::Message(_) => {}
        }
    }
    println!("Update-listening window completed.");
    Ok(0)
}

fn wait_for_response(
    server: &AppServer,
    id: u64,
    timeout: Duration,
) -> Result<IncomingMessage, String> {
    let deadline = Instant::now() + timeout;
    loop {
        match receive_until(server, deadline)? {
            TransportEvent::Message(message) if message.has_id(id) => return Ok(message),
            TransportEvent::Message(_) => {}
            TransportEvent::MalformedLine(detail) => {
                return Err(format!("App Server emitted malformed JSONL: {detail}"));
            }
        }
    }
}

fn receive_until(server: &AppServer, deadline: Instant) -> Result<TransportEvent, String> {
    let now = Instant::now();
    if now >= deadline {
        return Err("timed out waiting for App Server response".to_owned());
    }
    let timeout = deadline
        .saturating_duration_since(now)
        .min(Duration::from_secs(1));
    match server.receive(timeout)? {
        Some(event) => Ok(event),
        None => receive_until(server, deadline),
    }
}

fn reject_rpc_error(message: &IncomingMessage, operation: &str) -> Result<(), String> {
    if let Some(error) = message.error.as_ref() {
        return Err(format!(
            "{operation} returned JSON-RPC error code {}; server text was suppressed for privacy. Action: verify Codex login and schema version.",
            error.code
        ));
    }
    Ok(())
}

fn parse_result<T: serde::de::DeserializeOwned>(
    message: IncomingMessage,
    operation: &str,
) -> Result<T, String> {
    let result = message
        .result
        .ok_or_else(|| format!("{operation} response omitted result"))?;
    serde_json::from_value(result).map_err(|error| {
        format!(
            "{operation} response did not match generated schema at line {}, column {}",
            error.line(),
            error.column()
        )
    })
}

fn print_account_or_explain(account: &AccountState) -> Option<i32> {
    match account {
        AccountState::ChatGpt { plan_type } => {
            println!(
                "Account: ChatGPT ({})",
                plan_type.as_deref().unwrap_or("plan unknown")
            );
            None
        }
        AccountState::ApiKey => {
            eprintln!(
                "Quota unavailable: Codex is using API-key billing, which does not expose ChatGPT Codex quota windows."
            );
            Some(2)
        }
        AccountState::AmazonBedrock => {
            eprintln!(
                "Quota unavailable: Codex is using Amazon Bedrock, which does not expose ChatGPT Codex quota windows."
            );
            Some(2)
        }
        AccountState::Unauthenticated => {
            eprintln!("Quota unavailable: no authenticated Codex account was returned.");
            eprintln!("Action: run `codex login`, then retry this spike.");
            Some(2)
        }
        AccountState::Unavailable => {
            eprintln!("Quota unavailable: App Server returned no account.");
            eprintln!("Action: inspect `codex login status` and retry.");
            Some(2)
        }
        AccountState::Unsupported(kind) => {
            eprintln!("Quota unavailable: unsupported account type `{kind}`.");
            Some(2)
        }
    }
}

fn print_summary(title: &str, summary: &QuotaSummary) {
    println!("{title}:");
    for window in &summary.windows {
        println!(
            "  {}: {}% remaining ({}% used)",
            window.display_name(),
            window.remaining_percent,
            window.used_percent
        );
        println!(
            "    window: {}; reset: {}",
            window
                .window_duration_mins
                .map(|value| format!("{value} minutes"))
                .unwrap_or_else(|| "duration not provided".to_owned()),
            format_reset_time(window.resets_at)
        );
    }
    print_issues(summary);
}

fn print_issues(summary: &QuotaSummary) {
    for issue in &summary.issues {
        eprintln!("  Data warning [{}]: {}", issue.context, issue.detail);
    }
}
