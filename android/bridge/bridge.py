#!/usr/bin/env python3
"""Small, read-only Termux Bridge for Codex App Server.

The Bridge is the only App Server compatibility layer for the experimental
Android route. It binds its HTTP status endpoint to loopback, sends only the
approved read sequence, and never exposes raw JSON-RPC responses.
"""

from __future__ import annotations

import argparse
import copy
import datetime as datetime_module
import hashlib
import http.server
import json
import os
import queue
import signal
import subprocess
import sys
import threading
import time
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple
from urllib.parse import urlsplit


BRIDGE_SCHEMA_VERSION = 1
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 43127
DEFAULT_TIMEOUT_SECONDS = 30.0
DEFAULT_REFRESH_SECONDS = 300.0
DEFAULT_RETRY_SECONDS = 1.0
DEFAULT_MAX_RESTARTS = 3
TERMINATE_GRACE_SECONDS = 2.0


class BridgeError(Exception):
    """Sanitized transport/protocol failure."""

    def __init__(
        self,
        kind: str,
        stage: Optional[str] = None,
        code: Optional[int] = None,
        auth_hint: bool = False,
    ) -> None:
        super().__init__(kind)
        self.kind = kind
        self.stage = stage
        self.code = code
        self.auth_hint = auth_hint


def _safe_int(value: Any) -> Optional[int]:
    return value if type(value) is int else None


def _safe_string(value: Any) -> Optional[str]:
    return value.strip() if isinstance(value, str) and value.strip() else None


def _utc_now() -> str:
    return (
        datetime_module.datetime.now(datetime_module.timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )


def _unix_seconds(value: Any) -> Tuple[Optional[str], bool]:
    """Return an ISO timestamp and whether an explicitly supplied value is valid."""

    if value is None:
        return None, True
    if type(value) is not int or value < 0:
        return None, False
    try:
        timestamp = datetime_module.datetime.fromtimestamp(value, datetime_module.timezone.utc)
    except (OverflowError, OSError, ValueError):
        return None, False
    return timestamp.isoformat(timespec="seconds").replace("+00:00", "Z"), True


def _auth_hint(error: Any, code: Optional[int]) -> bool:
    if code in (401, 403, -32001, -32002):
        return True
    if not isinstance(error, dict):
        return False
    message = error.get("message")
    if not isinstance(message, str):
        return False
    lowered = message.lower()
    return any(
        term in lowered
        for term in (
            "unauthoriz",
            "unauthent",
            "not authenticated",
            "not logged",
            "login required",
            "sign in",
            "credential",
            "forbidden",
            "permission denied",
        )
    )


def _hash_key(identity: str, slot: str) -> str:
    return hashlib.sha256(f"{identity}\n{slot}".encode("utf-8")).hexdigest()


def _normal_plan(value: Any) -> Optional[str]:
    text = _safe_string(value)
    if not text:
        return None
    return text[0].upper() + text[1:].lower()


def _select_snapshots(result: Dict[str, Any]) -> Tuple[List[Tuple[Optional[str], Any]], bool, bool]:
    """Select multi-bucket data first and report source validity/presence."""

    buckets = result.get("rateLimitsByLimitId")
    if isinstance(buckets, dict) and buckets:
        return sorted(buckets.items(), key=lambda item: str(item[0])), True, False

    if "rateLimitsByLimitId" in result and buckets is not None and not isinstance(buckets, dict):
        return [], False, True

    if "rateLimits" in result:
        legacy = result.get("rateLimits")
        if isinstance(legacy, dict):
            return [(None, legacy)], True, False
        if legacy is None:
            return [], False, True
        return [], False, True

    if isinstance(buckets, dict) and not buckets:
        return [], True, False

    return [], False, False


def _normalize_reset_credits(result: Dict[str, Any]) -> Tuple[Dict[str, Any], int]:
    field_present = "rateLimitResetCredits" in result
    summary = result.get("rateLimitResetCredits")
    base: Dict[str, Any] = {
        "kind": "Unavailable",
        "availableCount": None,
        "creditDetailCount": None,
        "earliestKnownExpiry": None,
        "fieldPresent": field_present,
    }
    if not field_present or summary is None:
        return base, 0
    if not isinstance(summary, dict):
        return base, 1

    count = _safe_int(summary.get("availableCount"))
    if count is None:
        return base, 1
    issues = 0
    if count < 0:
        count = 0
        issues += 1

    credits = summary.get("credits")
    detail_count = len(credits) if isinstance(credits, list) else None
    if "credits" in summary and credits is not None and not isinstance(credits, list):
        issues += 1

    base["availableCount"] = count
    base["creditDetailCount"] = detail_count
    if count == 0:
        base["kind"] = "Empty"
        return base, issues

    expirations: List[str] = []
    if isinstance(credits, list):
        for credit in credits:
            if not isinstance(credit, dict) or "expiresAt" not in credit:
                continue
            expiry, valid = _unix_seconds(credit.get("expiresAt"))
            if valid and expiry is not None:
                expirations.append(expiry)
    if not expirations:
        base["kind"] = "CountOnly"
        return base, issues

    base["earliestKnownExpiry"] = min(expirations)
    base["kind"] = "CompleteDetails" if detail_count == count else "PartialDetails"
    return base, issues


def normalize_response(result: Any) -> Dict[str, Any]:
    """Normalize only the public state projection; raw response fields stay internal."""

    if not isinstance(result, dict):
        return {
            "state": "unavailable",
            "windowCount": 0,
            "windows": [],
            "resetCredits": {
                "kind": "Unavailable",
                "availableCount": None,
                "creditDetailCount": None,
                "earliestKnownExpiry": None,
                "fieldPresent": False,
            },
            "planType": None,
            "issueCount": 1,
        }

    entries, source_present, source_invalid = _select_snapshots(result)
    windows: List[Dict[str, Any]] = []
    window_issues = 1 if source_invalid else 0
    plan_type: Optional[str] = None
    fallback_ordinal = 0

    for bucket, snapshot in entries:
        if not isinstance(snapshot, dict):
            window_issues += 1
            continue

        if plan_type is None:
            plan_type = _normal_plan(snapshot.get("planType"))

        saw_window_field = False
        for slot in ("primary", "secondary"):
            if slot not in snapshot or snapshot.get(slot) is None:
                continue
            saw_window_field = True
            window = snapshot.get(slot)
            if not isinstance(window, dict):
                window_issues += 1
                continue

            used = _safe_int(window.get("usedPercent"))
            if used is None:
                window_issues += 1
                continue

            reliable = 0 <= used <= 100
            if not reliable:
                window_issues += 1
            clamped_used = max(0, min(100, used))

            duration: Optional[int] = None
            if "windowDurationMins" in window and window.get("windowDurationMins") is not None:
                duration = _safe_int(window.get("windowDurationMins"))
                if duration is None or duration <= 0:
                    duration = None
                    window_issues += 1

            reset_at: Optional[str] = None
            if "resetsAt" in window and window.get("resetsAt") is not None:
                reset_at, valid = _unix_seconds(window.get("resetsAt"))
                if not valid:
                    window_issues += 1

            limit_id = _safe_string(snapshot.get("limitId"))
            bucket_text = _safe_string(bucket)
            identity = limit_id or bucket_text
            if identity:
                local_key = _hash_key(identity, slot)
                alert_key = f"sha256:{local_key}" if limit_id else f"fallback:{slot}:{duration or 'unknown'}:{fallback_ordinal}"
            else:
                local_key = f"fallback:{slot}:{duration or 'unknown'}:{fallback_ordinal}"
                alert_key = local_key
            fallback_ordinal += 1

            windows.append(
                {
                    "localKey": local_key,
                    "alertKey": alert_key,
                    "limitName": _safe_string(snapshot.get("limitName")),
                    "sourceSlot": slot,
                    "usedPercent": clamped_used,
                    "remainingPercent": 100 - clamped_used,
                    "percentageReliable": reliable,
                    "windowDurationMinutes": duration,
                    "resetAtUtc": reset_at,
                }
            )

        if snapshot and not saw_window_field:
            window_issues += 1

    reset_credits, reset_issues = _normalize_reset_credits(result)
    if not entries and source_present and not source_invalid:
        quota_state = "zero_windows"
    elif windows and window_issues == 0:
        quota_state = "available"
    elif not windows and window_issues == 0 and source_present:
        quota_state = "zero_windows"
    else:
        quota_state = "unavailable"

    return {
        "state": quota_state,
        "windowCount": len(windows),
        "windows": windows,
        "resetCredits": reset_credits,
        "planType": plan_type,
        "issueCount": window_issues + reset_issues,
    }


def _merge_snapshot(baseline: Any, patch: Any) -> Dict[str, Any]:
    merged = copy.deepcopy(baseline) if isinstance(baseline, dict) else {}
    if not isinstance(patch, dict):
        return merged

    for key in ("limitId", "limitName", "planType"):
        if key in patch and patch.get(key) is not None:
            merged[key] = copy.deepcopy(patch[key])

    for slot in ("primary", "secondary"):
        if slot not in patch or patch.get(slot) is None:
            continue
        patch_window = patch.get(slot)
        if not isinstance(patch_window, dict):
            merged[slot] = copy.deepcopy(patch_window)
            continue
        old_window = merged.get(slot) if isinstance(merged.get(slot), dict) else {}
        old_window = copy.deepcopy(old_window)
        for key in ("usedPercent", "windowDurationMins", "resetsAt"):
            if key in patch_window and patch_window.get(key) is not None:
                old_window[key] = copy.deepcopy(patch_window[key])
        merged[slot] = old_window
    return merged


def merge_response(baseline: Any, patch: Any) -> Optional[Dict[str, Any]]:
    """Apply an App Server sparse notification without clearing omitted fields."""

    if not isinstance(patch, dict):
        return None
    if not isinstance(baseline, dict):
        return copy.deepcopy(patch) if _is_independent_snapshot(patch) else None

    merged = copy.deepcopy(baseline)
    if isinstance(patch.get("rateLimits"), dict):
        merged["rateLimits"] = _merge_snapshot(merged.get("rateLimits"), patch["rateLimits"])

    patch_buckets = patch.get("rateLimitsByLimitId")
    if isinstance(patch_buckets, dict):
        old_buckets = merged.get("rateLimitsByLimitId")
        buckets = copy.deepcopy(old_buckets) if isinstance(old_buckets, dict) else {}
        for key, value in patch_buckets.items():
            buckets[str(key)] = _merge_snapshot(buckets.get(str(key)), value)
        merged["rateLimitsByLimitId"] = buckets

    if "rateLimitResetCredits" in patch:
        merged["rateLimitResetCredits"] = copy.deepcopy(patch.get("rateLimitResetCredits"))
    return merged


def _complete_window(window: Any) -> bool:
    return (
        isinstance(window, dict)
        and _safe_int(window.get("usedPercent")) is not None
        and (_safe_int(window.get("windowDurationMins")) or 0) > 0
        and _safe_int(window.get("resetsAt")) is not None
    )


def _complete_snapshot(bucket: Any, snapshot: Any) -> bool:
    if not isinstance(snapshot, dict):
        return False
    identity = _safe_string(snapshot.get("limitId")) or _safe_string(bucket)
    return bool(identity) and _complete_window(snapshot.get("primary")) and _complete_window(snapshot.get("secondary"))


def _is_independent_snapshot(value: Any) -> bool:
    if not isinstance(value, dict):
        return False
    buckets = value.get("rateLimitsByLimitId")
    if isinstance(buckets, dict) and buckets:
        return all(_complete_snapshot(key, snapshot) for key, snapshot in buckets.items())
    legacy = value.get("rateLimits")
    return _complete_snapshot(None, legacy)


class StateStore:
    """Thread-safe normalized state and sanitized diagnostics."""

    def __init__(self, port: int) -> None:
        self._lock = threading.RLock()
        self._port = port
        self._protocol_snapshot: Optional[Dict[str, Any]] = None
        self._pending_patches: List[Dict[str, Any]] = []
        self._full_read_in_flight = False
        self._quota = normalize_response({})
        self._connection_state = "unavailable"
        self._authenticated: Optional[bool] = None
        self._refreshing = True
        self._last_success_at: Optional[str] = None
        self._last_error: Optional[str] = None
        self._requires_full_read = False
        self._diagnostics: Dict[str, Any] = {
            "malformedJsonCount": 0,
            "stderrObserved": False,
            "notificationCount": 0,
            "ignoredMessageCount": 0,
            "eofObserved": False,
            "processRunning": False,
            "processCleanupSucceeded": False,
            "processReturnCode": None,
            "startCount": 0,
            "restartCount": 0,
        }

    def set_port(self, port: int) -> None:
        with self._lock:
            self._port = port

    def begin_refresh(self) -> None:
        with self._lock:
            self._refreshing = True
            self._connection_state = "refreshing"

    def begin_full_read(self) -> None:
        with self._lock:
            self._full_read_in_flight = True

    def cancel_full_read(self) -> None:
        with self._lock:
            self._full_read_in_flight = False
            if self._protocol_snapshot is None:
                self._pending_patches.clear()
                return
            for patch in self._pending_patches:
                merged = merge_response(self._protocol_snapshot, patch)
                if merged is not None:
                    self._protocol_snapshot = merged
            self._pending_patches.clear()
            self._quota = normalize_response(self._protocol_snapshot)

    def apply_full(self, result: Any) -> None:
        if not isinstance(result, dict):
            raise BridgeError("protocol_error", stage="account/rateLimits/read")
        with self._lock:
            snapshot = copy.deepcopy(result)
            for patch in self._pending_patches:
                merged = merge_response(snapshot, patch)
                if merged is not None:
                    snapshot = merged
            self._pending_patches.clear()
            self._full_read_in_flight = False
            self._protocol_snapshot = snapshot
            self._quota = normalize_response(snapshot)
            self._connection_state = "fresh"
            self._authenticated = True
            self._refreshing = False
            self._last_success_at = _utc_now()
            self._last_error = None
            self._requires_full_read = False

    def apply_notification(self, params: Any) -> bool:
        if not isinstance(params, dict):
            with self._lock:
                self._requires_full_read = True
            return False
        with self._lock:
            if self._protocol_snapshot is None or self._full_read_in_flight:
                self._pending_patches.append(copy.deepcopy(params))
                self._requires_full_read = True
                return False
            merged = merge_response(self._protocol_snapshot, params)
            if merged is None:
                self._requires_full_read = True
                return False
            self._protocol_snapshot = merged
            self._quota = normalize_response(merged)
            self._connection_state = "fresh"
            self._refreshing = False
            self._last_success_at = _utc_now()
            self._last_error = None
            return True

    def mark_error(self, kind: str, auth_hint: bool = False) -> None:
        with self._lock:
            self._refreshing = False
            self._last_error = kind
            if auth_hint:
                self._authenticated = False
                self._connection_state = "unauthenticated"
            elif self._protocol_snapshot is not None:
                self._connection_state = "stale"
            elif kind == "protocol_error":
                self._connection_state = "unavailable"
            else:
                self._connection_state = "offline"

    def record_session(self, session: Optional["JsonlAppServer"]) -> None:
        if session is None:
            return
        with self._lock:
            self._diagnostics.update(session.diagnostics())

    def set_process_counts(self, start_count: int) -> None:
        with self._lock:
            self._diagnostics["startCount"] = start_count
            self._diagnostics["restartCount"] = max(0, start_count - 1)

    def snapshot(self) -> Dict[str, Any]:
        with self._lock:
            return {
                "schemaVersion": BRIDGE_SCHEMA_VERSION,
                "bridge": {
                    "bindAddress": DEFAULT_HOST,
                    "port": self._port,
                    "readOnly": True,
                },
                "connection": {
                    "state": self._connection_state,
                    "authenticated": self._authenticated,
                    "refreshing": self._refreshing,
                    "lastSuccessAt": self._last_success_at,
                    "lastError": self._last_error,
                    "requiresFullRead": self._requires_full_read,
                },
                "quota": copy.deepcopy(self._quota),
                "diagnostics": copy.deepcopy(self._diagnostics),
            }


class JsonlAppServer:
    """One App Server process with independent stdout/stderr drainers."""

    def __init__(
        self,
        command: Sequence[str],
        timeout_seconds: float,
        notification_handler: Callable[[Any], None],
    ) -> None:
        self.command = list(command)
        self.timeout_seconds = timeout_seconds
        self.notification_handler = notification_handler
        self.process: Optional[subprocess.Popen] = None
        self._responses: "queue.Queue[Any]" = queue.Queue()
        self._threads: List[threading.Thread] = []
        self._closed = False
        self._eof_observed = False
        self._malformed_json_count = 0
        self._stderr_observed = False
        self._notification_count = 0
        self._ignored_message_count = 0
        self._cleanup_succeeded = False
        self._return_code: Optional[int] = None

    def start(self) -> None:
        popen_kwargs: Dict[str, Any] = {
            "stdin": subprocess.PIPE,
            "stdout": subprocess.PIPE,
            "stderr": subprocess.PIPE,
            "bufsize": 0,
        }
        if os.name == "nt":
            popen_kwargs["creationflags"] = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        else:
            popen_kwargs["start_new_session"] = True
        try:
            self.process = subprocess.Popen(self.command, **popen_kwargs)
        except (FileNotFoundError, PermissionError, OSError) as error:
            raise BridgeError("startup_failed", stage="start") from error

        self._threads = [
            threading.Thread(target=self._read_stdout, name="bridge-stdout", daemon=True),
            threading.Thread(target=self._read_stderr, name="bridge-stderr", daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def send_request(self, request_id: int, method: str, params: Any) -> Any:
        self._send({"id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + self.timeout_seconds
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise BridgeError("timeout", stage=method)
            try:
                message = self._responses.get(timeout=remaining)
            except queue.Empty as error:
                raise BridgeError("timeout", stage=method) from error
            if message is _EOF:
                self._eof_observed = True
                raise BridgeError("eof", stage=method)
            response_id = message.get("id") if isinstance(message, dict) else None
            if type(response_id) is not int or response_id != request_id:
                self._ignored_message_count += 1
                continue
            if "error" in message:
                error_body = message.get("error")
                code = _safe_int(error_body.get("code")) if isinstance(error_body, dict) else None
                auth = _auth_hint(error_body, code)
                raise BridgeError("rpc_error", stage=method, code=code, auth_hint=auth)
            if "result" not in message:
                raise BridgeError("protocol_error", stage=method)
            return message["result"]

    def send_notification(self, method: str) -> None:
        self._send({"method": method})

    def _send(self, message: Dict[str, Any]) -> None:
        if self.process is None or self.process.stdin is None:
            raise BridgeError("startup_failed", stage="write")
        payload = (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode(
            "utf-8"
        )
        try:
            self.process.stdin.write(payload)
            self.process.stdin.flush()
        except (BrokenPipeError, OSError, ValueError) as error:
            raise BridgeError("eof", stage="write") from error

    def _read_stdout(self) -> None:
        stream = self.process.stdout if self.process is not None else None
        if stream is None:
            self._responses.put(_EOF)
            return
        try:
            for line in iter(stream.readline, b""):
                self._handle_stdout_line(line)
        except (OSError, ValueError):
            pass
        finally:
            self._responses.put(_EOF)

    def _handle_stdout_line(self, raw_line: bytes) -> None:
        try:
            message = json.loads(raw_line.decode("utf-8", errors="replace").strip())
        except (UnicodeDecodeError, json.JSONDecodeError):
            self._malformed_json_count += 1
            return
        if not isinstance(message, dict):
            self._malformed_json_count += 1
            return

        if "id" not in message:
            method = message.get("method")
            if method == "account/rateLimits/updated":
                self._notification_count += 1
                try:
                    self.notification_handler(message.get("params"))
                except Exception:
                    # A malformed notification must not kill the stdout drainer.
                    pass
            elif isinstance(method, str):
                self._notification_count += 1
            else:
                self._ignored_message_count += 1
            return
        self._responses.put(message)

    def _read_stderr(self) -> None:
        stream = self.process.stderr if self.process is not None else None
        if stream is None:
            return
        try:
            while True:
                block = stream.read(4096)
                if not block:
                    return
                self._stderr_observed = True
        except (OSError, ValueError):
            return

    def diagnostics(self) -> Dict[str, Any]:
        process = self.process
        running = process is not None and process.poll() is None
        return {
            "malformedJsonCount": self._malformed_json_count,
            "stderrObserved": self._stderr_observed,
            "notificationCount": self._notification_count,
            "ignoredMessageCount": self._ignored_message_count,
            "eofObserved": self._eof_observed,
            "processRunning": running,
            "processCleanupSucceeded": self._cleanup_succeeded,
            "processReturnCode": self._return_code,
        }

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        process = self.process
        if process is None:
            self._cleanup_succeeded = True
            return

        try:
            if process.stdin is not None:
                process.stdin.close()
        except (OSError, ValueError):
            pass

        if process.poll() is None:
            self._terminate_process(process)
        try:
            process.wait(timeout=TERMINATE_GRACE_SECONDS)
        except (subprocess.TimeoutExpired, OSError):
            pass

        self._return_code = process.poll()
        self._cleanup_succeeded = self._return_code is not None
        for thread in self._threads:
            thread.join(timeout=1.0)
        for stream in (process.stdout, process.stderr):
            if stream is not None:
                try:
                    stream.close()
                except (OSError, ValueError):
                    pass

    @staticmethod
    def _terminate_process(process: subprocess.Popen) -> None:
        try:
            if os.name == "nt":
                try:
                    process.send_signal(signal.CTRL_BREAK_EVENT)
                except (AttributeError, OSError, ValueError):
                    process.terminate()
            else:
                os.killpg(process.pid, signal.SIGTERM)
        except (ProcessLookupError, OSError, ValueError):
            try:
                process.terminate()
            except (OSError, ValueError):
                pass

        try:
            process.wait(timeout=TERMINATE_GRACE_SECONDS)
            return
        except (subprocess.TimeoutExpired, OSError):
            pass

        try:
            if os.name == "nt":
                process.kill()
            else:
                os.killpg(process.pid, signal.SIGKILL)
        except (ProcessLookupError, OSError, ValueError):
            try:
                process.kill()
            except (OSError, ValueError):
                pass


_EOF = object()


def _error_name(error: BridgeError) -> str:
    if error.auth_hint:
        return "unauthenticated"
    if error.stage == "initialize":
        return "initialize_failed"
    if error.kind == "startup_failed":
        return "startup_failed"
    if error.kind == "rpc_error":
        return "rate_limits_rpc_error"
    return error.kind


class BridgeRuntime:
    """Refresh worker, finite reconnect loop, and public state owner."""

    def __init__(
        self,
        codex_bin: str = "codex",
        codex_args: Sequence[str] = (),
        timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
        refresh_seconds: float = DEFAULT_REFRESH_SECONDS,
        retry_seconds: float = DEFAULT_RETRY_SECONDS,
        max_restarts: int = DEFAULT_MAX_RESTARTS,
        port: int = DEFAULT_PORT,
    ) -> None:
        self.command = [codex_bin, *codex_args, "app-server", "--stdio"]
        self.timeout_seconds = timeout_seconds
        self.refresh_seconds = refresh_seconds
        self.retry_seconds = retry_seconds
        self.max_restarts = max(1, max_restarts)
        self.store = StateStore(port)
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._session: Optional[JsonlAppServer] = None
        self._session_lock = threading.RLock()
        self._start_count = 0

    def set_port(self, port: int) -> None:
        self.store.set_port(port)

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="bridge-runtime", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._close_session()
        if self._thread is not None:
            self._thread.join(timeout=self.timeout_seconds + TERMINATE_GRACE_SECONDS + 1.0)
        self.store.record_session(self._session)

    def snapshot(self) -> Dict[str, Any]:
        self.store.record_session(self._session)
        return self.store.snapshot()

    def _run(self) -> None:
        attempts = 0
        try:
            while not self._stop.is_set():
                if self._session is None:
                    if attempts >= self.max_restarts:
                        self.store.mark_error("reconnect_exhausted")
                        if self._stop.wait(self.refresh_seconds):
                            break
                        attempts = 0
                        continue
                    self.store.begin_refresh()
                    try:
                        self.store.begin_full_read()
                        self._connect_and_read()
                        attempts = 0
                        continue
                    except BridgeError as error:
                        self.store.cancel_full_read()
                        attempts += 1
                        self.store.mark_error(_error_name(error), error.auth_hint)
                        self._close_session()
                        delay = min(self.retry_seconds * (2 ** max(0, attempts - 1)), self.refresh_seconds)
                        if self._stop.wait(delay):
                            break
                        continue

                if self._stop.wait(self.refresh_seconds):
                    break
                self.store.begin_refresh()
                try:
                    self.store.begin_full_read()
                    result = self._session.send_request(2, "account/rateLimits/read", None)
                    self.store.apply_full(result)
                except BridgeError as error:
                    self.store.cancel_full_read()
                    self.store.mark_error(_error_name(error), error.auth_hint)
                    self._close_session()
                    attempts = 0
        finally:
            self._close_session()

    def _connect_and_read(self) -> None:
        session = JsonlAppServer(
            self.command,
            self.timeout_seconds,
            notification_handler=self.store.apply_notification,
        )
        with self._session_lock:
            if self._stop.is_set():
                return
            self._session = session
            self._start_count += 1
            self.store.set_process_counts(self._start_count)
            session.start()
        self.store.record_session(session)

        initialize_result = session.send_request(
            1,
            "initialize",
            {
                "clientInfo": {
                    "name": "codex_quota_tray_android_bridge",
                    "title": "CodexQuotaTray Android Bridge",
                    "version": "0.0.0-p1",
                }
            },
        )
        if not isinstance(initialize_result, dict):
            raise BridgeError("protocol_error", stage="initialize")
        session.send_notification("initialized")
        result = session.send_request(2, "account/rateLimits/read", None)
        self.store.apply_full(result)
        self.store.record_session(session)

    def _close_session(self) -> None:
        with self._session_lock:
            session = self._session
            self._session = None
        if session is None:
            return
        session.close()
        self.store.record_session(session)


class _BridgeHttpHandler(http.server.BaseHTTPRequestHandler):
    server_version = "CodexQuotaTrayBridge/1"
    sys_version = ""

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        bridge_server = self.server
        path = urlsplit(self.path).path
        if path == "/v1/status":
            self._write_json(200, bridge_server.runtime.snapshot())
            return
        if path == "/healthz":
            self._write_json(200, {"schemaVersion": BRIDGE_SCHEMA_VERSION, "status": "ok"})
            return
        self._write_json(404, {"schemaVersion": BRIDGE_SCHEMA_VERSION, "error": "not_found"})

    def _write_json(self, status: int, value: Dict[str, Any]) -> None:
        payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode(
            "utf-8"
        )
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format: str, *args: Any) -> None:
        return


class BridgeHTTPServer(http.server.ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, server_address: Tuple[str, int], runtime: BridgeRuntime) -> None:
        self.runtime = runtime
        super().__init__(server_address, _BridgeHttpHandler)
        self.runtime.set_port(self.server_port)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the read-only Termux Codex quota Bridge.")
    parser.add_argument("--codex-bin", default="codex", help="Codex executable; default: codex")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="loopback HTTP port; default: 43127")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_SECONDS)
    parser.add_argument("--refresh-seconds", type=float, default=DEFAULT_REFRESH_SECONDS)
    parser.add_argument("--retry-seconds", type=float, default=DEFAULT_RETRY_SECONDS)
    parser.add_argument("--max-restarts", type=int, default=DEFAULT_MAX_RESTARTS)
    parser.add_argument(
        "--codex-arg",
        action="append",
        default=[],
        help="extra executable argument, intended for the local fake upstream",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if not 0 <= args.port <= 65535:
        parser.error("--port must be between 0 and 65535")
    if args.timeout <= 0 or args.refresh_seconds <= 0 or args.retry_seconds <= 0:
        parser.error("timeouts and intervals must be greater than zero")
    if args.max_restarts < 0:
        parser.error("--max-restarts must not be negative")

    runtime = BridgeRuntime(
        codex_bin=args.codex_bin,
        codex_args=args.codex_arg,
        timeout_seconds=args.timeout,
        refresh_seconds=args.refresh_seconds,
        retry_seconds=args.retry_seconds,
        max_restarts=args.max_restarts,
        port=args.port,
    )
    server = BridgeHTTPServer((DEFAULT_HOST, args.port), runtime)
    runtime.start()
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()
        server.server_close()
        runtime.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
