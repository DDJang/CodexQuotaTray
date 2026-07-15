use std::ffi::OsString;
use std::fmt;
use std::time::{Duration, Instant};

use codex_quota_tray::app_server::AppServerLaunch;
use codex_quota_tray::compatibility::{
    VersionCompatibility, evaluate_user_agent, schema_codex_version,
};
use codex_quota_tray::json_rpc::{ClientEvent, JsonRpcClient, ProtocolDiagnostic, RpcClientError};
use codex_quota_tray::protocol::{
    ACCOUNT_READ_METHOD, AccountRateLimitsUpdatedNotification, AccountReadResponse,
    INITIALIZE_METHOD, INITIALIZED_METHOD, InitializeResponse, RATE_LIMITS_READ_METHOD,
    RATE_LIMITS_UPDATED_METHOD, RateLimitsReadResponse, account_read_params, initialize_params,
    rate_limits_read_params,
};
use codex_quota_tray::quota::{
    AccountState, QuotaSummary, account_state, format_reset_time, summarize_rate_limits,
};
use codex_quota_tray::supervisor::{
    AppServerSupervisor, RestartPolicy, SupervisorEvent, SupervisorReport,
};

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

#[derive(Debug)]
struct SessionFailure {
    message: String,
    recoverable_transport: bool,
}

impl SessionFailure {
    fn from_rpc(operation: &str, error: RpcClientError) -> Self {
        let recoverable_transport = matches!(
            error,
            RpcClientError::Transport { .. }
                | RpcClientError::TransportClosed
                | RpcClientError::ClientStopped
        );
        Self {
            message: format!(
                "{operation} failed: {error}. Action: verify Codex login, CLI availability, and schema version."
            ),
            recoverable_transport,
        }
    }
}

impl From<String> for SessionFailure {
    fn from(message: String) -> Self {
        Self {
            message,
            recoverable_transport: false,
        }
    }
}

impl fmt::Display for SessionFailure {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
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
    let launch = AppServerLaunch::codex(options.codex_bin);
    let mut supervisor = match AppServerSupervisor::start(launch, RestartPolicy::default()) {
        Ok(supervisor) => supervisor,
        Err(message) => {
            eprintln!("Codex App Server supervisor unavailable: {message}");
            return 1;
        }
    };

    let session_code = run_supervised_session(&supervisor, options.watch_seconds);
    match supervisor.shutdown() {
        Ok(report) => finish_supervised_session(session_code, &report),
        Err(message) => {
            eprintln!("Could not cleanly stop App Server supervisor: {message}");
            1
        }
    }
}

fn run_supervised_session(supervisor: &AppServerSupervisor, watch_seconds: u64) -> i32 {
    loop {
        let event = match supervisor.next_event(Duration::from_secs(1)) {
            Ok(Some(event)) => event,
            Ok(None) => continue,
            Err(message) => {
                eprintln!("App Server supervisor failed: {message}");
                return 1;
            }
        };

        match event {
            SupervisorEvent::Starting { .. } => {}
            SupervisorEvent::Started(connection) => {
                let generation = connection.generation();
                let client = JsonRpcClient::new(connection.server());
                let result = run_session(&client, watch_seconds);
                drop(client);

                match result {
                    Ok(code) => return code,
                    Err(failure) if failure.recoverable_transport => {
                        eprintln!("App Server generation {generation} transport failed: {failure}");
                        if let Err(message) = supervisor.request_restart() {
                            eprintln!("Could not request App Server recovery: {message}");
                            return 1;
                        }
                    }
                    Err(failure) => {
                        eprintln!("P0 spike failed: {failure}");
                        return 1;
                    }
                }
            }
            SupervisorEvent::SpawnFailed { generation } => {
                eprintln!("App Server generation {generation} could not be started.");
            }
            SupervisorEvent::ProcessExited {
                generation, code, ..
            } => {
                eprintln!(
                    "App Server generation {generation} exited unexpectedly with status {code:?}."
                );
            }
            SupervisorEvent::Backoff { attempt, delay } => {
                eprintln!(
                    "App Server restart attempt {attempt} will begin after {} ms.",
                    delay.as_millis()
                );
            }
            SupervisorEvent::Exhausted { restart_count } => {
                eprintln!(
                    "App Server recovery stopped after {restart_count} bounded restart attempts."
                );
                eprintln!(
                    "Action: verify Codex CLI installation and login, then restart CodexQuotaTray."
                );
                return 1;
            }
            SupervisorEvent::RestartRequested { .. } => {}
        }
    }
}

fn finish_supervised_session(session_code: i32, report: &SupervisorReport) -> i32 {
    if report.stderr_observed {
        eprintln!("Note: App Server wrote diagnostics to stderr; raw text was suppressed.");
    }
    if report.forced_terminations > 0 {
        eprintln!(
            "App Server required {} forced termination(s) after graceful shutdown timed out.",
            report.forced_terminations
        );
        1
    } else {
        session_code
    }
}

fn run_session(client: &JsonRpcClient, watch_seconds: u64) -> Result<i32, SessionFailure> {
    let initialize_result = client
        .request(INITIALIZE_METHOD, initialize_params(), INITIALIZE_TIMEOUT)
        .map_err(|error| SessionFailure::from_rpc(INITIALIZE_METHOD, error))?;
    let initialize: InitializeResponse = parse_result(initialize_result, INITIALIZE_METHOD)?;

    println!("Codex App Server: {}", initialize.user_agent);
    println!(
        "Platform: {}/{}; generated schema: codex-cli {}",
        initialize.platform_family,
        initialize.platform_os,
        schema_codex_version()
    );
    match evaluate_user_agent(&initialize.user_agent, schema_codex_version()) {
        VersionCompatibility::Mismatch {
            schema_version,
            runtime_version,
        } => eprintln!(
            "Warning: running App Server version {runtime_version} differs from generated schema {schema_version}; regenerate schemas before relying on new fields."
        ),
        VersionCompatibility::Unreported { schema_version } => eprintln!(
            "Warning: running App Server did not advertise a parseable version; generated schema is {schema_version}."
        ),
        VersionCompatibility::Unknown | VersionCompatibility::Match { .. } => {}
    }

    client
        .notify(INITIALIZED_METHOD, None)
        .map_err(|error| SessionFailure::from_rpc(INITIALIZED_METHOD, error))?;
    let account_request = client
        .start_request(ACCOUNT_READ_METHOD, account_read_params(), READ_TIMEOUT)
        .map_err(|error| SessionFailure::from_rpc(ACCOUNT_READ_METHOD, error))?;
    let rate_limits_request = client
        .start_request(
            RATE_LIMITS_READ_METHOD,
            rate_limits_read_params(),
            READ_TIMEOUT,
        )
        .map_err(|error| SessionFailure::from_rpc(RATE_LIMITS_READ_METHOD, error))?;

    let account_response: AccountReadResponse = parse_result(
        account_request
            .wait()
            .map_err(|error| SessionFailure::from_rpc(ACCOUNT_READ_METHOD, error))?,
        ACCOUNT_READ_METHOD,
    )?;
    let mut rate_limits_response: RateLimitsReadResponse = parse_result(
        rate_limits_request
            .wait()
            .map_err(|error| SessionFailure::from_rpc(RATE_LIMITS_READ_METHOD, error))?,
        RATE_LIMITS_READ_METHOD,
    )?;

    let account = account_state(&account_response);
    if let Some(code) = print_account_or_explain(&account) {
        return Ok(code);
    }

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
        "Reset credits: unavailable (codex-cli {} does not expose a reset-credit count)",
        schema_codex_version()
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
        let Some(event) = client
            .receive_event(timeout)
            .map_err(|error| SessionFailure::from_rpc("notification stream", error))?
        else {
            continue;
        };

        match event {
            ClientEvent::Notification(notification)
                if notification.method == RATE_LIMITS_UPDATED_METHOD =>
            {
                let params = notification.params.ok_or_else(|| {
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
            ClientEvent::Diagnostic(diagnostic) => {
                eprintln!(
                    "Protocol warning: {}",
                    describe_protocol_diagnostic(&diagnostic)
                );
            }
            ClientEvent::Notification(_) => {}
        }
    }
    println!("Update-listening window completed.");
    Ok(0)
}

fn parse_result<T: serde::de::DeserializeOwned>(
    result: serde_json::Value,
    operation: &str,
) -> Result<T, String> {
    serde_json::from_value(result).map_err(|error| {
        format!(
            "{operation} response did not match generated schema at line {}, column {}",
            error.line(),
            error.column()
        )
    })
}

fn describe_protocol_diagnostic(diagnostic: &ProtocolDiagnostic) -> &'static str {
    match diagnostic {
        ProtocolDiagnostic::MalformedJson => "discarded malformed JSON from App Server stdout",
        ProtocolDiagnostic::InvalidEnvelope => "discarded an invalid JSON-RPC envelope",
        ProtocolDiagnostic::UnsupportedResponseId => {
            "discarded a response whose ID type was not a client-generated integer"
        }
        ProtocolDiagnostic::UnknownResponseId { .. } => {
            "discarded a response for an unknown or expired request ID"
        }
        ProtocolDiagnostic::DuplicateResponse { .. } => {
            "discarded a duplicate response for an already completed request"
        }
    }
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
