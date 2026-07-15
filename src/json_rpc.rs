use std::collections::{HashMap, VecDeque};
use std::fmt;
use std::mem;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use serde_json::{Map, Value, json};

use crate::app_server::{AppServer, TransportEvent};

pub type RequestId = i64;

const DISPATCH_POLL_INTERVAL: Duration = Duration::from_millis(25);
const COMPLETED_ID_HISTORY: usize = 256;

pub trait RpcTransport: Send + Sync + 'static {
    fn send(&self, message: &Value) -> Result<(), String>;
    fn receive(&self, timeout: Duration) -> Result<Option<TransportEvent>, String>;
}

impl RpcTransport for Mutex<AppServer> {
    fn send(&self, message: &Value) -> Result<(), String> {
        lock(self)
            .map_err(|_| "App Server transport lock was poisoned".to_owned())?
            .send(message)
    }

    fn receive(&self, timeout: Duration) -> Result<Option<TransportEvent>, String> {
        lock(self)
            .map_err(|_| "App Server transport lock was poisoned".to_owned())?
            .receive(timeout)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProtocolIssue {
    InvalidResponseEnvelope,
    InvalidErrorObject,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RpcClientError {
    RequestIdExhausted,
    Timeout {
        request_id: RequestId,
    },
    Remote {
        request_id: RequestId,
        code: i64,
    },
    Protocol {
        request_id: RequestId,
        issue: ProtocolIssue,
    },
    Transport {
        detail: String,
    },
    TransportClosed,
    ClientStopped,
}

impl fmt::Display for RpcClientError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::RequestIdExhausted => write!(formatter, "JSON-RPC request IDs were exhausted"),
            Self::Timeout { request_id } => {
                write!(formatter, "request {request_id} timed out")
            }
            Self::Remote { request_id, code } => write!(
                formatter,
                "request {request_id} returned JSON-RPC error code {code}; server text was suppressed for privacy"
            ),
            Self::Protocol { request_id, issue } => {
                write!(formatter, "request {request_id} received {issue:?}")
            }
            Self::Transport { detail } => write!(formatter, "JSON-RPC transport failed: {detail}"),
            Self::TransportClosed => write!(formatter, "App Server stdout closed"),
            Self::ClientStopped => write!(formatter, "JSON-RPC client stopped"),
        }
    }
}

impl std::error::Error for RpcClientError {}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProtocolDiagnostic {
    MalformedJson,
    InvalidEnvelope,
    UnsupportedResponseId,
    UnknownResponseId { request_id: RequestId },
    DuplicateResponse { request_id: RequestId },
}

#[derive(Debug, Clone, PartialEq)]
pub struct ServerNotification {
    pub method: String,
    pub params: Option<Value>,
}

#[derive(Debug, Clone, PartialEq)]
pub enum ClientEvent {
    Notification(ServerNotification),
    Diagnostic(ProtocolDiagnostic),
}

type PendingResult = Result<Value, RpcClientError>;

struct SharedState {
    next_id: AtomicI64,
    pending: Mutex<HashMap<RequestId, Sender<PendingResult>>>,
    completed: Mutex<VecDeque<RequestId>>,
    closed: AtomicBool,
    stop: AtomicBool,
}

impl SharedState {
    fn new() -> Self {
        Self {
            next_id: AtomicI64::new(0),
            pending: Mutex::new(HashMap::new()),
            completed: Mutex::new(VecDeque::new()),
            closed: AtomicBool::new(false),
            stop: AtomicBool::new(false),
        }
    }

    fn next_request_id(&self) -> Result<RequestId, RpcClientError> {
        self.next_id
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |current| {
                current.checked_add(1)
            })
            .map_err(|_| RpcClientError::RequestIdExhausted)
    }

    fn register(
        &self,
        request_id: RequestId,
        sender: Sender<PendingResult>,
    ) -> Result<(), RpcClientError> {
        let mut pending = lock_unpoisoned(&self.pending);
        if self.closed.load(Ordering::Acquire) || self.stop.load(Ordering::Acquire) {
            return Err(RpcClientError::TransportClosed);
        }
        pending.insert(request_id, sender);
        Ok(())
    }

    fn cancel(&self, request_id: RequestId) {
        lock_unpoisoned(&self.pending).remove(&request_id);
    }

    fn complete(&self, request_id: RequestId, result: PendingResult) -> Option<ProtocolDiagnostic> {
        let sender = lock_unpoisoned(&self.pending).remove(&request_id);
        if let Some(sender) = sender {
            self.record_completed(request_id);
            let _ = sender.send(result);
            return None;
        }

        let completed = lock_unpoisoned(&self.completed);
        if completed.contains(&request_id) {
            Some(ProtocolDiagnostic::DuplicateResponse { request_id })
        } else {
            Some(ProtocolDiagnostic::UnknownResponseId { request_id })
        }
    }

    fn record_completed(&self, request_id: RequestId) {
        let mut completed = lock_unpoisoned(&self.completed);
        completed.push_back(request_id);
        if completed.len() > COMPLETED_ID_HISTORY {
            completed.pop_front();
        }
    }

    fn fail_all(&self, error: RpcClientError) {
        let pending = {
            let mut pending = lock_unpoisoned(&self.pending);
            self.closed.store(true, Ordering::Release);
            mem::take(&mut *pending)
        };
        for (_, sender) in pending {
            let _ = sender.send(Err(error.clone()));
        }
    }
}

pub struct PendingRequest {
    request_id: RequestId,
    deadline: Instant,
    receiver: Receiver<PendingResult>,
    state: Arc<SharedState>,
    finished: bool,
}

impl PendingRequest {
    pub fn id(&self) -> RequestId {
        self.request_id
    }

    pub fn wait(mut self) -> PendingResult {
        let remaining = self.deadline.saturating_duration_since(Instant::now());
        let result = match self.receiver.recv_timeout(remaining) {
            Ok(result) => result,
            Err(RecvTimeoutError::Timeout) => {
                self.state.cancel(self.request_id);
                Err(RpcClientError::Timeout {
                    request_id: self.request_id,
                })
            }
            Err(RecvTimeoutError::Disconnected) => Err(RpcClientError::ClientStopped),
        };
        self.finished = true;
        result
    }
}

impl Drop for PendingRequest {
    fn drop(&mut self) {
        if !self.finished {
            self.state.cancel(self.request_id);
        }
    }
}

pub struct JsonRpcClient {
    transport: Arc<dyn RpcTransport>,
    state: Arc<SharedState>,
    event_receiver: Mutex<Receiver<ClientEvent>>,
    dispatcher: Option<JoinHandle<()>>,
}

impl JsonRpcClient {
    pub fn new<T>(transport: Arc<T>) -> Self
    where
        T: RpcTransport,
    {
        let transport: Arc<dyn RpcTransport> = transport;
        let state = Arc::new(SharedState::new());
        let (event_sender, event_receiver) = mpsc::channel();
        let dispatcher_transport = Arc::clone(&transport);
        let dispatcher_state = Arc::clone(&state);
        let dispatcher = thread::spawn(move || {
            dispatch_loop(dispatcher_transport, dispatcher_state, event_sender)
        });

        Self {
            transport,
            state,
            event_receiver: Mutex::new(event_receiver),
            dispatcher: Some(dispatcher),
        }
    }

    pub fn start_request(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
    ) -> Result<PendingRequest, RpcClientError> {
        let started_at = Instant::now();
        let deadline = started_at.checked_add(timeout).unwrap_or(started_at);
        let request_id = self.state.next_request_id()?;
        let (sender, receiver) = mpsc::channel();
        self.state.register(request_id, sender)?;

        let message = json!({
            "method": method,
            "id": request_id,
            "params": params,
        });
        if let Err(detail) = self.transport.send(&message) {
            self.state.fail_all(RpcClientError::TransportClosed);
            return Err(RpcClientError::Transport { detail });
        }

        Ok(PendingRequest {
            request_id,
            deadline,
            receiver,
            state: Arc::clone(&self.state),
            finished: false,
        })
    }

    pub fn request(&self, method: &str, params: Value, timeout: Duration) -> PendingResult {
        self.start_request(method, params, timeout)?.wait()
    }

    pub fn notify(&self, method: &str, params: Option<Value>) -> Result<(), RpcClientError> {
        if self.state.closed.load(Ordering::Acquire) {
            return Err(RpcClientError::TransportClosed);
        }

        let mut message = Map::new();
        message.insert("method".to_owned(), Value::String(method.to_owned()));
        if let Some(params) = params {
            message.insert("params".to_owned(), params);
        }

        if let Err(detail) = self.transport.send(&Value::Object(message)) {
            self.state.fail_all(RpcClientError::TransportClosed);
            return Err(RpcClientError::Transport { detail });
        }
        Ok(())
    }

    pub fn receive_event(&self, timeout: Duration) -> Result<Option<ClientEvent>, RpcClientError> {
        match lock_unpoisoned(&self.event_receiver).recv_timeout(timeout) {
            Ok(event) => Ok(Some(event)),
            Err(RecvTimeoutError::Timeout) => Ok(None),
            Err(RecvTimeoutError::Disconnected) => Err(RpcClientError::TransportClosed),
        }
    }
}

impl Drop for JsonRpcClient {
    fn drop(&mut self) {
        self.state.stop.store(true, Ordering::Release);
        if let Some(dispatcher) = self.dispatcher.take() {
            let _ = dispatcher.join();
        }
        self.state.fail_all(RpcClientError::ClientStopped);
    }
}

fn dispatch_loop(
    transport: Arc<dyn RpcTransport>,
    state: Arc<SharedState>,
    event_sender: Sender<ClientEvent>,
) {
    while !state.stop.load(Ordering::Acquire) {
        match transport.receive(DISPATCH_POLL_INTERVAL) {
            Ok(Some(TransportEvent::Message(message))) => {
                dispatch_message(message, &state, &event_sender);
            }
            Ok(Some(TransportEvent::MalformedLine(_))) => {
                let _ =
                    event_sender.send(ClientEvent::Diagnostic(ProtocolDiagnostic::MalformedJson));
            }
            Ok(None) => {}
            Err(_) => {
                state.fail_all(RpcClientError::TransportClosed);
                return;
            }
        }
    }
    state.fail_all(RpcClientError::ClientStopped);
}

fn dispatch_message(message: Value, state: &SharedState, event_sender: &Sender<ClientEvent>) {
    let Some(object) = message.as_object() else {
        emit_diagnostic(event_sender, ProtocolDiagnostic::InvalidEnvelope);
        return;
    };

    let id = object.get("id");
    let method = object.get("method");
    let has_result = object.contains_key("result");
    let has_error = object.contains_key("error");

    if id.is_none() && !has_result && !has_error {
        if let Some(method) = method.and_then(Value::as_str) {
            let notification = ServerNotification {
                method: method.to_owned(),
                params: object.get("params").cloned(),
            };
            let _ = event_sender.send(ClientEvent::Notification(notification));
        } else {
            emit_diagnostic(event_sender, ProtocolDiagnostic::InvalidEnvelope);
        }
        return;
    }

    let Some(request_id) = id.and_then(Value::as_i64) else {
        emit_diagnostic(event_sender, ProtocolDiagnostic::UnsupportedResponseId);
        return;
    };

    if method.is_some() || has_result == has_error {
        emit_diagnostic(event_sender, ProtocolDiagnostic::InvalidEnvelope);
        complete_invalid_response(
            state,
            event_sender,
            request_id,
            ProtocolIssue::InvalidResponseEnvelope,
        );
        return;
    }

    let result = if has_result {
        Ok(object.get("result").cloned().unwrap_or(Value::Null))
    } else {
        match parse_error_code(object.get("error")) {
            Some(code) => Err(RpcClientError::Remote { request_id, code }),
            None => {
                emit_diagnostic(event_sender, ProtocolDiagnostic::InvalidEnvelope);
                complete_invalid_response(
                    state,
                    event_sender,
                    request_id,
                    ProtocolIssue::InvalidErrorObject,
                );
                return;
            }
        }
    };

    if let Some(diagnostic) = state.complete(request_id, result) {
        emit_diagnostic(event_sender, diagnostic);
    }
}

fn complete_invalid_response(
    state: &SharedState,
    event_sender: &Sender<ClientEvent>,
    request_id: RequestId,
    issue: ProtocolIssue,
) {
    if let Some(diagnostic) = state.complete(
        request_id,
        Err(RpcClientError::Protocol { request_id, issue }),
    ) {
        emit_diagnostic(event_sender, diagnostic);
    }
}

fn parse_error_code(error: Option<&Value>) -> Option<i64> {
    let error = error?.as_object()?;
    error.get("message")?.as_str()?;
    error.get("code")?.as_i64()
}

fn emit_diagnostic(sender: &Sender<ClientEvent>, diagnostic: ProtocolDiagnostic) {
    let _ = sender.send(ClientEvent::Diagnostic(diagnostic));
}

fn lock<T>(mutex: &Mutex<T>) -> Result<MutexGuard<'_, T>, ()> {
    mutex.lock().map_err(|_| ())
}

fn lock_unpoisoned<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}
