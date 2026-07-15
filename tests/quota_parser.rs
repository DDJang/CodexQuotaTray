use codex_quota_tray::protocol::{
    AccountRateLimitsUpdatedNotification, AccountReadResponse, IncomingMessage,
    RateLimitsReadResponse, account_read_request, initialize_request, initialized_notification,
    rate_limits_read_request,
};
use codex_quota_tray::quota::{
    AccountState, ResetCreditsState, account_state, duration_name, summarize_rate_limits,
};
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

fn envelope_fixture(name: &str) -> IncomingMessage {
    let contents = match name {
        "account" => include_str!("fixtures/account_chatgpt_redacted.json"),
        "single" => include_str!("fixtures/rate_limits_single_weekly.json"),
        "dual" => include_str!("fixtures/rate_limits_dual_window.json"),
        "multi" => include_str!("fixtures/rate_limits_multi_bucket.json"),
        "sparse" => include_str!("fixtures/rate_limits_sparse_update.json"),
        "missing" => include_str!("fixtures/rate_limits_missing_fields.json"),
        _ => panic!("unknown fixture"),
    };
    serde_json::from_str(contents).expect("fixture must contain a valid envelope")
}

fn result<T: DeserializeOwned>(message: IncomingMessage) -> T {
    serde_json::from_value(message.result.expect("fixture result")).expect("typed fixture result")
}

#[test]
fn account_fixture_discards_email_from_typed_and_debug_output() {
    let response: AccountReadResponse = result(envelope_fixture("account"));
    assert_eq!(
        account_state(&response),
        AccountState::ChatGpt {
            plan_type: Some("plus".to_owned())
        }
    );
    assert!(!format!("{response:?}").contains("REDACTED"));
}

#[test]
fn account_modes_are_actionable_without_live_service() {
    let api_key: AccountReadResponse = serde_json::from_value(json!({
        "account": { "type": "apiKey" },
        "requiresOpenaiAuth": true
    }))
    .unwrap();
    let bedrock: AccountReadResponse = serde_json::from_value(json!({
        "account": { "type": "amazonBedrock" },
        "requiresOpenaiAuth": false
    }))
    .unwrap();
    let logged_out: AccountReadResponse = serde_json::from_value(json!({
        "account": null,
        "requiresOpenaiAuth": true
    }))
    .unwrap();

    assert_eq!(account_state(&api_key), AccountState::ApiKey);
    assert_eq!(account_state(&bedrock), AccountState::AmazonBedrock);
    assert_eq!(account_state(&logged_out), AccountState::Unauthenticated);
}

#[test]
fn primary_can_be_a_weekly_window() {
    let response: RateLimitsReadResponse = result(envelope_fixture("single"));
    let summary = summarize_rate_limits(&response);
    assert_eq!(summary.windows.len(), 1);
    assert_eq!(summary.windows[0].source_slot, "primary");
    assert_eq!(summary.windows[0].window_duration_mins, Some(10_080));
    assert_eq!(summary.windows[0].remaining_percent, 72);
    assert_eq!(summary.windows[0].display_name(), "7-day quota");
    assert_eq!(
        summary.reset_credits,
        ResetCreditsState::UnavailableInSchema
    );
}

#[test]
fn dual_windows_are_named_by_duration_not_slot() {
    let response: RateLimitsReadResponse = result(envelope_fixture("dual"));
    let summary = summarize_rate_limits(&response);
    assert_eq!(summary.windows.len(), 2);
    assert_eq!(summary.windows[0].display_name(), "Codex (5-hour)");
    assert_eq!(summary.windows[1].display_name(), "Codex (7-day)");
}

#[test]
fn multi_bucket_view_wins_over_legacy_and_unknown_duration_is_dynamic() {
    let response: RateLimitsReadResponse = result(envelope_fixture("multi"));
    let summary = summarize_rate_limits(&response);
    assert_eq!(summary.windows.len(), 2);
    assert_eq!(summary.windows[0].used_percent, 25);
    assert_eq!(summary.windows[1].limit_id.as_deref(), Some("other"));
    assert_eq!(summary.windows[1].display_name(), "Additional (90-minute)");
    assert_eq!(duration_name(Some(1_440)), "1-day");
}

#[test]
fn missing_used_percent_is_not_replaced_with_zero() {
    let response: RateLimitsReadResponse = result(envelope_fixture("missing"));
    let summary = summarize_rate_limits(&response);
    assert!(summary.windows.is_empty());
    assert_eq!(summary.issues.len(), 1);
    assert!(summary.issues[0].detail.contains("not replaced with zero"));
}

#[test]
fn out_of_range_percentage_is_clamped_with_warning() {
    let response: RateLimitsReadResponse = serde_json::from_value(json!({
        "rateLimits": {
            "limitId": "codex",
            "primary": { "usedPercent": 125, "windowDurationMins": 300 }
        }
    }))
    .unwrap();
    let summary = summarize_rate_limits(&response);
    assert_eq!(summary.windows[0].used_percent, 100);
    assert_eq!(summary.windows[0].remaining_percent, 0);
    assert_eq!(summary.issues.len(), 1);
}

#[test]
fn sparse_notification_merges_without_clearing_metadata() {
    let mut response: RateLimitsReadResponse = result(envelope_fixture("single"));
    let notification_message = envelope_fixture("sparse");
    let notification: AccountRateLimitsUpdatedNotification =
        serde_json::from_value(notification_message.params.unwrap()).unwrap();

    response.merge_sparse_notification(notification.rate_limits);
    let summary = summarize_rate_limits(&response);
    assert_eq!(summary.windows[0].used_percent, 35);
    assert_eq!(summary.windows[0].window_duration_mins, Some(10_080));
    assert_eq!(summary.windows[0].limit_id.as_deref(), Some("codex"));
}

#[test]
fn malformed_fixture_fails_without_live_account() {
    let error = serde_json::from_str::<IncomingMessage>(include_str!("fixtures/malformed.json"))
        .expect_err("fixture must be malformed");
    assert!(error.is_eof());
}

#[test]
fn outgoing_messages_are_read_only_and_match_schema() {
    let messages = [
        initialize_request(),
        initialized_notification(),
        account_read_request(),
        rate_limits_read_request(),
    ];
    let methods: Vec<&str> = messages
        .iter()
        .map(|message| message["method"].as_str().unwrap())
        .collect();
    assert_eq!(
        methods,
        [
            "initialize",
            "initialized",
            "account/read",
            "account/rateLimits/read"
        ]
    );
    assert!(messages[1].get("params").is_none());
    assert_eq!(messages[2]["params"]["refreshToken"], Value::Bool(false));
    assert!(messages[3]["params"].is_null());

    let serialized = serde_json::to_string(&messages).unwrap();
    assert!(!serialized.contains("consume"));
    assert!(!serialized.contains("token"));
}
