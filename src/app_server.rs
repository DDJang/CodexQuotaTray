use std::ffi::OsString;
use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, Command, ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use serde_json::Value;

const DEFAULT_SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(3);

#[derive(Debug)]
pub enum TransportEvent {
    Message(Value),
    MalformedLine(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ShutdownReport {
    pub forced: bool,
    pub exit_code: Option<i32>,
    pub stderr_observed: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ProcessExit {
    pub code: Option<i32>,
    pub success: bool,
}

#[derive(Clone)]
struct LaunchCommand {
    program: OsString,
    label: &'static str,
    args: Vec<OsString>,
    env: Vec<(OsString, OsString)>,
}

#[derive(Clone)]
pub struct AppServerLaunch {
    candidates: Vec<LaunchCommand>,
    shutdown_timeout: Duration,
}

impl AppServerLaunch {
    pub fn codex(explicit_binary: Option<OsString>) -> Self {
        let args = vec![OsString::from("app-server"), OsString::from("--stdio")];
        let candidates = if let Some(binary) = explicit_binary {
            vec![LaunchCommand {
                program: binary,
                label: "explicit --codex-bin",
                args,
                env: Vec::new(),
            }]
        } else {
            default_candidates(args)
        };

        Self {
            candidates,
            shutdown_timeout: DEFAULT_SHUTDOWN_TIMEOUT,
        }
    }

    pub fn custom(
        program: OsString,
        args: Vec<OsString>,
        env: Vec<(OsString, OsString)>,
        shutdown_timeout: Duration,
    ) -> Self {
        Self {
            candidates: vec![LaunchCommand {
                program,
                label: "custom App Server command",
                args,
                env,
            }],
            shutdown_timeout,
        }
    }
}

pub struct AppServer {
    child: Child,
    stdin: Option<ChildStdin>,
    receiver: Receiver<TransportEvent>,
    reader_thread: Option<JoinHandle<()>>,
    stderr_thread: Option<JoinHandle<()>>,
    stderr_observed: Arc<AtomicBool>,
    shutdown_timeout: Duration,
    shutdown_report: Option<ShutdownReport>,
}

impl AppServer {
    pub fn spawn(explicit_binary: Option<OsString>) -> Result<Self, String> {
        Self::spawn_launch(&AppServerLaunch::codex(explicit_binary))
    }

    pub fn spawn_launch(launch: &AppServerLaunch) -> Result<Self, String> {
        let mut failures = Vec::new();

        for candidate in &launch.candidates {
            let mut command = Command::new(&candidate.program);
            command
                .args(&candidate.args)
                .envs(candidate.env.iter().cloned())
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
                    let Some(stdin) = child.stdin.take() else {
                        reap_incomplete_child(&mut child);
                        return Err("App Server stdin pipe was unavailable".to_owned());
                    };
                    let Some(stdout) = child.stdout.take() else {
                        reap_incomplete_child(&mut child);
                        return Err("App Server stdout pipe was unavailable".to_owned());
                    };
                    let Some(stderr) = child.stderr.take() else {
                        reap_incomplete_child(&mut child);
                        return Err("App Server stderr pipe was unavailable".to_owned());
                    };

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

                    let stderr_observed = Arc::new(AtomicBool::new(false));
                    let thread_stderr_observed = Arc::clone(&stderr_observed);
                    let stderr_thread = thread::spawn(move || {
                        let mut reader = BufReader::new(stderr);
                        let mut buffer = [0_u8; 4096];
                        loop {
                            match reader.read(&mut buffer) {
                                Ok(0) => break,
                                Ok(_) => {
                                    thread_stderr_observed.store(true, Ordering::Release);
                                }
                                Err(_) => break,
                            }
                        }
                    });

                    return Ok(Self {
                        child,
                        stdin: Some(stdin),
                        receiver,
                        reader_thread: Some(reader_thread),
                        stderr_thread: Some(stderr_thread),
                        stderr_observed,
                        shutdown_timeout: launch.shutdown_timeout,
                        shutdown_report: None,
                    });
                }
                Err(error) => failures.push(format!("{}: {:?}", candidate.label, error.kind())),
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

    pub fn try_exit(&mut self) -> Result<Option<ProcessExit>, String> {
        if let Some(report) = self.shutdown_report.as_ref() {
            return Ok(Some(ProcessExit {
                code: report.exit_code,
                success: report.exit_code == Some(0),
            }));
        }

        self.child
            .try_wait()
            .map(|status| status.map(process_exit))
            .map_err(|error| format!("failed to poll App Server: {:?}", error.kind()))
    }

    pub fn shutdown(&mut self) -> Result<ShutdownReport, String> {
        if let Some(report) = self.shutdown_report.as_ref() {
            return Ok(report.clone());
        }

        self.stdin.take();
        let now = Instant::now();
        let deadline = now.checked_add(self.shutdown_timeout).unwrap_or(now);
        let mut status = None;
        while Instant::now() < deadline {
            match self.child.try_wait() {
                Ok(Some(exit_status)) => {
                    status = Some(exit_status);
                    break;
                }
                Ok(None) => thread::sleep(Duration::from_millis(20)),
                Err(error) => {
                    return Err(format!(
                        "failed while waiting for App Server: {:?}",
                        error.kind()
                    ));
                }
            }
        }

        let mut forced = status.is_none();
        if status.is_none()
            && let Err(error) = self.child.kill()
        {
            match self.child.try_wait() {
                Ok(Some(exit_status)) => {
                    status = Some(exit_status);
                    forced = false;
                }
                _ => {
                    return Err(format!(
                        "failed to terminate App Server: {:?}",
                        error.kind()
                    ));
                }
            }
        }
        if status.is_none() {
            status = Some(
                self.child
                    .wait()
                    .map_err(|error| format!("failed to reap App Server: {:?}", error.kind()))?,
            );
        }

        join_thread(&mut self.reader_thread);
        join_thread(&mut self.stderr_thread);

        let report = ShutdownReport {
            forced,
            exit_code: status.and_then(|value| value.code()),
            stderr_observed: self.stderr_observed.load(Ordering::Acquire),
        };
        self.shutdown_report = Some(report.clone());
        Ok(report)
    }
}

impl Drop for AppServer {
    fn drop(&mut self) {
        let _ = self.shutdown();
    }
}

fn default_candidates(args: Vec<OsString>) -> Vec<LaunchCommand> {
    #[cfg(windows)]
    {
        vec![
            LaunchCommand {
                program: OsString::from("codex.cmd"),
                label: "codex.cmd",
                args: args.clone(),
                env: Vec::new(),
            },
            LaunchCommand {
                program: OsString::from("codex.exe"),
                label: "codex.exe",
                args: args.clone(),
                env: Vec::new(),
            },
            LaunchCommand {
                program: OsString::from("codex"),
                label: "codex",
                args,
                env: Vec::new(),
            },
        ]
    }

    #[cfg(not(windows))]
    {
        vec![LaunchCommand {
            program: OsString::from("codex"),
            label: "codex",
            args,
            env: Vec::new(),
        }]
    }
}

fn process_exit(status: ExitStatus) -> ProcessExit {
    ProcessExit {
        code: status.code(),
        success: status.success(),
    }
}

fn join_thread(handle: &mut Option<JoinHandle<()>>) {
    if let Some(handle) = handle.take() {
        let _ = handle.join();
    }
}

fn reap_incomplete_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}
