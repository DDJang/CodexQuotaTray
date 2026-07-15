use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use codex_quota_tray::app_server::TransportEvent;
use codex_quota_tray::json_rpc::{
    ClientEvent, JsonRpcClient, ProtocolDiagnostic, ProtocolIssue, RpcClientError, RpcTransport,
};
use serde_json::{Value, json};

const TEST_TIMEOUT: Duration = Duration::from_secs(1);

struct FakeTransport {
    incoming: Mutex<Receiver<TransportEvent>>,
    outgoing: Sender<Value>,
}

impl RpcTransport for FakeTransport {
    fn send(&self, message: &Value) -> Result<(), String> {
        self.outgoing
            .send(message.clone())
            .map_err(|_| "fake stdin closed".to_owned())
    }

    fn receive(&self, timeout: Duration) -> Result<Option<TransportEvent>, String> {
        match self
            .incoming
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .recv_timeout(timeout)
        {
            Ok(event) => Ok(Some(event)),
            Err(RecvTimeoutError::Timeout) => Ok(None),
            Err(RecvTimeoutError::Disconnected) => Err("fake stdout closed".to_owned()),
        }
    }
}

fn harness() -> (JsonRpcClient, Sender<TransportEvent>, Receiver<Value>) {
    let (incoming_sender, incoming_receiver) = mpsc::channel();
    let (outgoing_sender, outgoing_receiver) = mpsc::channel();
    let transport = Arc::new(FakeTransport {
        incoming: Mutex::new(incoming_receiver),
        outgoing: outgoing_sender,
    });
    (
        JsonRpcClient::new(transport),
        incoming_sender,
        outgoing_receiver,
    )
}

fn sent_message(receiver: &Receiver<Value>) -> Value {
    receiver
        .recv_timeout(TEST_TIMEOUT)
        .expect("client should write a request")
}

fn next_event(client: &JsonRpcClient) -> ClientEvent {
    client
        .receive_event(TEST_TIMEOUT)
        .expect("event stream should remain open")
        .expect("dispatcher should emit an event")
}

#[test]
fn concurrent_requests_have_unique_ids_and_match_out_of_order_responses() {
    let (client, incoming, outgoing) = harness();
    let (first, second) = thread::scope(|scope| {
        let first = scope.spawn(|| {
            client
                .start_request("test/first", json!({ "value": 1 }), TEST_TIMEOUT)
                .unwrap()
        });
        let second = scope.spawn(|| {
            client
                .start_request("test/second", json!({ "value": 2 }), TEST_TIMEOUT)
                .unwrap()
        });
        (first.join().unwrap(), second.join().unwrap())
    });

    assert_ne!(first.id(), second.id());
    let first_wire = sent_message(&outgoing);
    let second_wire = sent_message(&outgoing);
    let wire_ids = [first_wire["id"].as_i64(), second_wire["id"].as_i64()];
    assert!(wire_ids.contains(&Some(first.id())));
    assert!(wire_ids.contains(&Some(second.id())));

    incoming
        .send(TransportEvent::Message(json!({
            "id": second.id(),
            "result": { "owner": "second" }
        })))
        .unwrap();
    incoming
        .send(TransportEvent::Message(json!({
            "id": first.id(),
            "result": { "owner": "first" }
        })))
        .unwrap();

    assert_eq!(first.wait().unwrap()["owner"], "first");
    assert_eq!(second.wait().unwrap()["owner"], "second");
}

#[test]
fn success_error_and_notification_are_distinct_and_error_text_is_suppressed() {
    let (client, incoming, outgoing) = harness();
    let request = client
        .start_request("test/error", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let _ = sent_message(&outgoing);

    incoming
        .send(TransportEvent::Message(json!({
            "method": "account/rateLimits/updated",
            "params": { "rateLimits": {} }
        })))
        .unwrap();
    incoming
        .send(TransportEvent::Message(json!({
            "id": request.id(),
            "error": {
                "code": 401,
                "message": "sensitive server detail must never escape"
            }
        })))
        .unwrap();

    let ClientEvent::Notification(notification) = next_event(&client) else {
        panic!("expected a server notification");
    };
    assert_eq!(notification.method, "account/rateLimits/updated");
    assert!(notification.params.is_some());

    let error = request.wait().unwrap_err();
    assert_eq!(
        error,
        RpcClientError::Remote {
            request_id: 0,
            code: 401
        }
    );
    assert!(!error.to_string().contains("sensitive server detail"));
}

#[test]
fn request_timeout_removes_pending_request_and_late_response_is_safe() {
    let (client, incoming, outgoing) = harness();
    let request = client
        .start_request("test/timeout", Value::Null, Duration::from_millis(20))
        .unwrap();
    let request_id = request.id();
    let _ = sent_message(&outgoing);

    assert_eq!(
        request.wait().unwrap_err(),
        RpcClientError::Timeout { request_id }
    );

    incoming
        .send(TransportEvent::Message(json!({
            "id": request_id,
            "result": null
        })))
        .unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::UnknownResponseId { request_id })
    );
}

#[test]
fn stdout_close_fails_every_pending_request() {
    let (client, incoming, outgoing) = harness();
    let first = client
        .start_request("test/first", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let second = client
        .start_request("test/second", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let _ = sent_message(&outgoing);
    let _ = sent_message(&outgoing);

    drop(incoming);

    assert_eq!(first.wait().unwrap_err(), RpcClientError::TransportClosed);
    assert_eq!(second.wait().unwrap_err(), RpcClientError::TransportClosed);
}

#[test]
fn unknown_duplicate_malformed_and_invalid_messages_do_not_break_client() {
    let (client, incoming, outgoing) = harness();

    incoming
        .send(TransportEvent::Message(json!({
            "id": 999,
            "result": { "ignored": true }
        })))
        .unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::UnknownResponseId { request_id: 999 })
    );

    incoming
        .send(TransportEvent::Message(json!({
            "id": "server-string-id",
            "result": { "ignored": true }
        })))
        .unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::UnsupportedResponseId)
    );

    let request = client
        .start_request("test/null", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let _ = sent_message(&outgoing);
    let response = json!({ "id": request.id(), "result": null });
    incoming
        .send(TransportEvent::Message(response.clone()))
        .unwrap();
    assert_eq!(request.wait().unwrap(), Value::Null);

    incoming.send(TransportEvent::Message(response)).unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::DuplicateResponse { request_id: 0 })
    );

    incoming
        .send(TransportEvent::MalformedLine(
            "synthetic line/column only".to_owned(),
        ))
        .unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::MalformedJson)
    );

    incoming
        .send(TransportEvent::Message(json!(["not", "an", "object"])))
        .unwrap();
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::InvalidEnvelope)
    );

    let after_diagnostics = client
        .start_request("test/still-healthy", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let _ = sent_message(&outgoing);
    incoming
        .send(TransportEvent::Message(json!({
            "id": after_diagnostics.id(),
            "result": { "healthy": true }
        })))
        .unwrap();
    assert_eq!(after_diagnostics.wait().unwrap()["healthy"], true);
}

#[test]
fn notification_has_no_id_and_does_not_consume_a_request_id() {
    let (client, _incoming, outgoing) = harness();
    client.notify("initialized", None).unwrap();
    let notification = sent_message(&outgoing);
    assert_eq!(notification["method"], "initialized");
    assert!(notification.get("id").is_none());
    assert!(notification.get("params").is_none());

    let request = client
        .start_request("test/after-notification", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let wire_request = sent_message(&outgoing);
    assert_eq!(request.id(), 0);
    assert_eq!(wire_request["id"], 0);
}

#[test]
fn invalid_error_response_fails_only_the_matching_request() {
    let (client, incoming, outgoing) = harness();
    let request = client
        .start_request("test/invalid-error", Value::Null, TEST_TIMEOUT)
        .unwrap();
    let request_id = request.id();
    let _ = sent_message(&outgoing);

    incoming
        .send(TransportEvent::Message(json!({
            "id": request_id,
            "error": { "code": "not-an-integer", "message": "suppressed" }
        })))
        .unwrap();

    assert_eq!(
        request.wait().unwrap_err(),
        RpcClientError::Protocol {
            request_id,
            issue: ProtocolIssue::InvalidErrorObject
        }
    );
    assert_eq!(
        next_event(&client),
        ClientEvent::Diagnostic(ProtocolDiagnostic::InvalidEnvelope)
    );
}
