use std::ffi::OsString;
use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use serde_json::Value;

const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(3);

#[derive(Debug)]
pub enum TransportEvent {
    Message(Value),
    MalformedLine(String),
}

#[derive(Debug)]
pub struct ShutdownReport {
    pub forced: bool,
    pub exit_code: Option<i32>,
    pub stderr_observed: bool,
}

pub struct AppServer {
    child: Child,
    stdin: Option<ChildStdin>,
    receiver: Receiver<TransportEvent>,
    reader_thread: Option<JoinHandle<()>>,
    stderr_thread: Option<JoinHandle<bool>>,
    stopped: bool,
}

impl AppServer {
    pub fn spawn(explicit_binary: Option<OsString>) -> Result<Self, String> {
        let candidates = candidates(explicit_binary);
        let mut failures = Vec::new();

        for (program, label) in candidates {
            let mut command = Command::new(&program);
            command
                .args(["app-server", "--stdio"])
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped());

            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                command.creation_flags(0x0800_0000);
            }

            match command.spawn() {
                Ok(mut child) => {
                    let stdin = child
                        .stdin
                        .take()
                        .ok_or_else(|| "App Server stdin pipe was unavailable".to_owned())?;
                    let stdout = child
                        .stdout
                        .take()
                        .ok_or_else(|| "App Server stdout pipe was unavailable".to_owned())?;
                    let stderr = child
                        .stderr
                        .take()
                        .ok_or_else(|| "App Server stderr pipe was unavailable".to_owned())?;

                    let (sender, receiver) = mpsc::channel();
                    let reader_thread = thread::spawn(move || {
                        let reader = BufReader::new(stdout);
                        for line in reader.lines() {
                            let event = match line {
                                Ok(line) => match serde_json::from_str::<Value>(&line) {
                                    Ok(message) => TransportEvent::Message(message),
                                    Err(error) => TransportEvent::MalformedLine(format!(
                                        "invalid JSONL message at line {}, column {}",
                                        error.line(),
                                        error.column()
                                    )),
                                },
                                Err(error) => TransportEvent::MalformedLine(format!(
                                    "stdout read failed with {:?}",
                                    error.kind()
                                )),
                            };
                            if sender.send(event).is_err() {
                                break;
                            }
                        }
                    });

                    let stderr_thread = thread::spawn(move || {
                        let mut reader = BufReader::new(stderr);
                        let mut buffer = [0_u8; 4096];
                        let mut observed = false;
                        loop {
                            match reader.read(&mut buffer) {
                                Ok(0) => break,
                                Ok(_) => observed = true,
                                Err(_) => break,
                            }
                        }
                        observed
                    });

                    return Ok(Self {
                        child,
                        stdin: Some(stdin),
                        receiver,
                        reader_thread: Some(reader_thread),
                        stderr_thread: Some(stderr_thread),
                        stopped: false,
                    });
                }
                Err(error) => failures.push(format!("{label}: {:?}", error.kind())),
            }
        }

        Err(format!(
            "could not start Codex App Server ({})",
            failures.join(", ")
        ))
    }

    pub fn send(&mut self, message: &Value) -> Result<(), String> {
        let stdin = self
            .stdin
            .as_mut()
            .ok_or_else(|| "App Server stdin was already closed".to_owned())?;
        serde_json::to_writer(&mut *stdin, message)
            .map_err(|_| "failed to serialize an App Server request".to_owned())?;
        stdin
            .write_all(b"\n")
            .and_then(|_| stdin.flush())
            .map_err(|error| format!("failed to write App Server stdin: {:?}", error.kind()))
    }

    pub fn receive(&self, timeout: Duration) -> Result<Option<TransportEvent>, String> {
        match self.receiver.recv_timeout(timeout) {
            Ok(event) => Ok(Some(event)),
            Err(RecvTimeoutError::Timeout) => Ok(None),
            Err(RecvTimeoutError::Disconnected) => {
                Err("App Server stdout closed before the session completed".to_owned())
            }
        }
    }

    pub fn shutdown(&mut self) -> Result<ShutdownReport, String> {
        if self.stopped {
            return Err("App Server was already stopped".to_owned());
        }

        self.stdin.take();
        let deadline = Instant::now() + SHUTDOWN_TIMEOUT;
        let mut status = None;
        while Instant::now() < deadline {
            match self.child.try_wait() {
                Ok(Some(exit_status)) => {
                    status = Some(exit_status);
                    break;
                }
                Ok(None) => thread::sleep(Duration::from_millis(50)),
                Err(error) => {
                    return Err(format!(
                        "failed while waiting for App Server: {:?}",
                        error.kind()
                    ));
                }
            }
        }

        let forced = status.is_none();
        if forced {
            self.child
                .kill()
                .map_err(|error| format!("failed to terminate App Server: {:?}", error.kind()))?;
            status = Some(
                self.child
                    .wait()
                    .map_err(|error| format!("failed to reap App Server: {:?}", error.kind()))?,
            );
        }

        if let Some(handle) = self.reader_thread.take() {
            let _ = handle.join();
        }
        let stderr_observed = self
            .stderr_thread
            .take()
            .and_then(|handle| handle.join().ok())
            .unwrap_or(false);
        self.stopped = true;

        Ok(ShutdownReport {
            forced,
            exit_code: status.and_then(|value| value.code()),
            stderr_observed,
        })
    }
}

impl Drop for AppServer {
    fn drop(&mut self) {
        if !self.stopped {
            let _ = self.shutdown();
        }
    }
}

fn candidates(explicit_binary: Option<OsString>) -> Vec<(OsString, &'static str)> {
    if let Some(binary) = explicit_binary {
        return vec![(binary, "explicit --codex-bin")];
    }

    #[cfg(windows)]
    {
        vec![
            (OsString::from("codex.cmd"), "codex.cmd"),
            (OsString::from("codex.exe"), "codex.exe"),
            (OsString::from("codex"), "codex"),
        ]
    }

    #[cfg(not(windows))]
    {
        vec![(OsString::from("codex"), "codex")]
    }
}
