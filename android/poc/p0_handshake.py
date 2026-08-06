#!/usr/bin/env python3
"""Small, dependency-free P0 probe for Codex App Server over JSONL.

The normal command is exactly: <codex-bin> app-server --stdio.
--codex-arg exists only to run the local fake process during offline checks.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import queue
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple


DEFAULT_TIMEOUT_SECONDS = 30.0
VERSION_TIMEOUT_SECONDS = 5.0
TERMINATE_GRACE_SECONDS = 2.0
VERSION_PATTERN = re.compile(r"(?<!\d)\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?")
AUTH_HINTS = (
    "unauthoriz",
    "unauthent",
    "not authenticated",
    "not logged",
    "login required",
    "sign in",
    "credential",
    "access token",
    "refresh token",
    "forbidden",
    "permission denied",
)


class ProbeFailure(Exception):
    """A sanitized failure that is safe to expose in the summary."""

    def __init__(
        self,
        kind: str,
        stage: Optional[str] = None,
        code: Optional[int] = None,
        auth_hint: bool = False,
        method_not_found: bool = False,
    ) -> None:
        super().__init__(kind)
        self.kind = kind
        self.stage = stage
        self.code = code
        self.auth_hint = auth_hint
        self.method_not_found = method_not_found


def _architecture() -> str:
    value = platform.machine()
    return value or "unavailable"


def _resolve_binary(value: str) -> str:
    resolved = shutil.which(value)
    if resolved:
        return os.path.realpath(resolved)
    return os.path.realpath(os.path.expanduser(value))


def _sha256_file(path: str) -> Optional[str]:
    candidate = Path(path)
    if not candidate.is_file():
        return None

    digest = hashlib.sha256()
    try:
        with candidate.open("rb") as stream:
            while True:
                block = stream.read(1024 * 1024)
                if not block:
                    break
                digest.update(block)
    except OSError:
        return None
    return digest.hexdigest()


def _safe_int(value: Any) -> Optional[int]:
    if type(value) is int:
        return value
    return None


def _auth_hint(error: Any, code: Optional[int]) -> bool:
    if code in (401, 403, -32001, -32002):
        return True
    if not isinstance(error, dict):
        return False
    message = error.get("message")
    if not isinstance(message, str):
        return False
    lowered = message.lower()
    return any(term in lowered for term in AUTH_HINTS)


def _error_kind(failure: ProbeFailure) -> str:
    if failure.kind == "rpc_error":
        if failure.auth_hint:
            return "unauthenticated"
        if failure.method_not_found:
            return "unsupported"
        return "rpc_error"
    return failure.kind


def _count_quota_windows(result: Any) -> Tuple[int, str]:
    """Count only windows with an explicit usedPercent field.

    This is intentionally not the production normalizer. P0 only needs a
    safe count and an explicit zero/unavailable distinction.
    """

    if not isinstance(result, dict):
        return 0, "unavailable"

    snapshots: List[Any] = []
    buckets = result.get("rateLimitsByLimitId")
    if isinstance(buckets, dict) and buckets:
        snapshots.extend(buckets.values())
    elif "rateLimits" in result and isinstance(result.get("rateLimits"), dict):
        snapshots.append(result["rateLimits"])
    elif isinstance(buckets, dict) and not buckets:
        if "rateLimits" not in result:
            return 0, "zero_windows"
        if not isinstance(result.get("rateLimits"), dict):
            return 0, "unavailable"
        snapshots.append(result["rateLimits"])
    else:
        return 0, "unavailable"

    count = 0
    incomplete = False
    for snapshot in snapshots:
        if not isinstance(snapshot, dict):
            incomplete = True
            continue
        saw_window_field = False
        for slot in ("primary", "secondary"):
            if slot not in snapshot:
                continue
            saw_window_field = True
            window = snapshot[slot]
            if isinstance(window, dict) and type(window.get("usedPercent")) is int:
                count += 1
            else:
                incomplete = True
        if snapshot and not saw_window_field:
            incomplete = True

    if incomplete:
        return count, "unavailable"
    return count, "available" if count else "zero_windows"


class JsonLineSession:
    """One App Server process with independent stdout/stderr draining."""

    def __init__(self, process: subprocess.Popen[bytes]) -> None:
        self.process = process
        self.events: "queue.Queue[Tuple[str, Any]]" = queue.Queue()
        self.malformed_json_count = 0
        self.stderr_observed = False
        self.eof_observed = False
        self.notification_count = 0
        self.ignored_message_count = 0
        self._threads = [
            threading.Thread(target=self._read_stdout, name="p0-stdout", daemon=True),
            threading.Thread(target=self._read_stderr, name="p0-stderr", daemon=True),
        ]
        for thread in self._threads:
            thread.start()

    def _read_stdout(self) -> None:
        stream = self.process.stdout
        if stream is None:
            self.events.put(("eof", None))
            return
        try:
            for line in iter(stream.readline, b""):
                self.events.put(("stdout", line))
        except (OSError, ValueError):
            pass
        finally:
            self.events.put(("eof", None))

    def _read_stderr(self) -> None:
        stream = self.process.stderr
        if stream is None:
            return
        try:
            while True:
                block = stream.read(4096)
                if not block:
                    return
                self.stderr_observed = True
        except (OSError, ValueError):
            return

    def send_request(self, request_id: int, method: str, params: Any) -> None:
        self._send({"id": request_id, "method": method, "params": params})

    def send_notification(self, method: str) -> None:
        self._send({"method": method})

    def _send(self, message: Dict[str, Any]) -> None:
        stream = self.process.stdin
        if stream is None:
            raise ProbeFailure("eof")
        payload = (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode(
            "utf-8"
        )
        try:
            stream.write(payload)
            stream.flush()
        except (BrokenPipeError, OSError, ValueError):
            raise ProbeFailure("eof")

    def wait_for_response(self, request_id: int, deadline: float, stage: str) -> Any:
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ProbeFailure("timeout", stage=stage)
            try:
                kind, payload = self.events.get(timeout=remaining)
            except queue.Empty:
                raise ProbeFailure("timeout", stage=stage)

            if kind == "eof":
                self.eof_observed = True
                raise ProbeFailure("eof", stage=stage)
            if kind != "stdout":
                continue

            message = self._parse_line(payload)
            if message is None:
                continue

            if "id" not in message:
                if isinstance(message.get("method"), str):
                    self.notification_count += 1
                else:
                    self.malformed_json_count += 1
                continue

            if type(message.get("id")) is not int or message["id"] != request_id:
                self.ignored_message_count += 1
                continue

            if "error" in message:
                error = message.get("error")
                code = _safe_int(error.get("code")) if isinstance(error, dict) else None
                raise ProbeFailure(
                    "rpc_error",
                    stage=stage,
                    code=code,
                    auth_hint=_auth_hint(error, code),
                    method_not_found=code == -32601,
                )
            if "result" not in message:
                raise ProbeFailure("protocol_error", stage=stage)
            return message["result"]

    def _parse_line(self, raw_line: bytes) -> Optional[Dict[str, Any]]:
        try:
            line = raw_line.decode("utf-8", errors="replace").strip()
            message = json.loads(line)
        except (UnicodeDecodeError, json.JSONDecodeError):
            self.malformed_json_count += 1
            return None

        if not isinstance(message, dict):
            self.malformed_json_count += 1
            return None
        return message

    def join_readers(self) -> None:
        for thread in self._threads:
            thread.join(timeout=1.0)


class P0Probe:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.process: Optional[subprocess.Popen[bytes]] = None
        self.session: Optional[JsonLineSession] = None
        self.binary = _resolve_binary(args.codex_bin)
        self.deadline = time.monotonic() + args.timeout
        self.summary: Dict[str, Any] = {
            "device_architecture": _architecture(),
            "codex_binary": self.binary,
            "codex_version": "unreported",
            "codex_version_probe_succeeded": False,
            "codex_build_source": args.codex_source or "not_recorded",
            "codex_commit_or_tag": args.codex_revision or "not_recorded",
            "codex_sha256": _sha256_file(self.binary),
            "app_server_started": False,
            "initialize_succeeded": False,
            "initialize_platform_family": None,
            "initialize_platform_os": None,
            "account_read_result": "not_requested",
            "account_read_rpc_code": None,
            "rate_limits_read_succeeded": False,
            "rate_limits_read_result": "not_attempted",
            "rate_limits_rpc_code": None,
            "quota_window_count": 0,
            "quota_state": "not_read",
            "authenticated": None,
            "malformed_json_count": 0,
            "stderr_observed": False,
            "eof_observed": False,
            "notification_count": 0,
            "ignored_message_count": 0,
            "process_cleanup_succeeded": False,
            "process_return_code": None,
            "success": False,
            "last_error": None,
        }

    def run(self) -> int:
        try:
            self._probe_version()
            self._start_process()
            self._initialize()
            if self.args.probe_account_read:
                self._probe_account_read()
            self._read_rate_limits()
            self.summary["success"] = bool(self.summary["rate_limits_read_succeeded"])
        except KeyboardInterrupt:
            self.summary["last_error"] = "interrupted"
        except ProbeFailure as failure:
            if failure.auth_hint:
                self.summary["authenticated"] = False
            self.summary["last_error"] = self._top_level_error(failure)
        finally:
            self._cleanup()
            self._copy_session_diagnostics()
            if self.summary["success"] and self.summary["malformed_json_count"] > 0:
                self.summary["success"] = False
                self.summary["last_error"] = "malformed_json"

        self.summary["exit_code"] = self._exit_code()
        return int(self.summary["exit_code"])

    def _probe_version(self) -> None:
        remaining = self._remaining()
        if remaining <= 0:
            raise ProbeFailure("timeout", stage="version")
        command = [self.binary] + list(self.args.codex_arg) + ["--version"]
        try:
            completed = subprocess.run(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=min(VERSION_TIMEOUT_SECONDS, remaining),
                check=False,
            )
        except (FileNotFoundError, PermissionError, OSError):
            return
        except subprocess.TimeoutExpired:
            return

        output = completed.stdout.decode("utf-8", errors="replace")
        match = VERSION_PATTERN.search(output)
        if match:
            self.summary["codex_version"] = match.group(0)
            self.summary["codex_version_probe_succeeded"] = completed.returncode == 0

    def _start_process(self) -> None:
        command = [self.binary] + list(self.args.codex_arg) + ["app-server", "--stdio"]
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
            self.process = subprocess.Popen(command, **popen_kwargs)
        except (FileNotFoundError, PermissionError, OSError):
            raise ProbeFailure("startup_failed", stage="start")

        self.summary["app_server_started"] = True
        self.session = JsonLineSession(self.process)

    def _initialize(self) -> None:
        result = self._request(
            request_id=1,
            method="initialize",
            params={
                "clientInfo": {
                    "name": "codex_quota_tray_android_poc",
                    "title": "CodexQuotaTray Android P0",
                    "version": "0.0.0-p0",
                }
            },
            stage="initialize",
        )
        if not isinstance(result, dict):
            raise ProbeFailure("protocol_error", stage="initialize")

        self.summary["initialize_succeeded"] = True
        self.summary["initialize_platform_family"] = _safe_string(result.get("platformFamily"))
        self.summary["initialize_platform_os"] = _safe_string(result.get("platformOs"))
        self._send_notification("initialized")

    def _probe_account_read(self) -> None:
        try:
            self._request(2, "account/read", None, "account/read")
        except ProbeFailure as failure:
            self.summary["account_read_result"] = _error_kind(failure)
            self.summary["account_read_rpc_code"] = failure.code
            if failure.auth_hint:
                self.summary["authenticated"] = False
            return

        self.summary["account_read_result"] = "succeeded"
        if self.summary["authenticated"] is None:
            self.summary["authenticated"] = True

    def _read_rate_limits(self) -> None:
        request_id = 3 if self.args.probe_account_read else 2
        try:
            result = self._request(request_id, "account/rateLimits/read", None, "account/rateLimits/read")
        except ProbeFailure as failure:
            self.summary["rate_limits_read_result"] = _error_kind(failure)
            self.summary["rate_limits_rpc_code"] = failure.code
            if failure.auth_hint:
                self.summary["authenticated"] = False
                self.summary["quota_state"] = "unauthenticated"
                self.summary["last_error"] = "unauthenticated"
            elif failure.kind == "rpc_error":
                self.summary["last_error"] = "rate_limits_rpc_error"
            else:
                self.summary["last_error"] = self._top_level_error(failure)
            return

        if not isinstance(result, dict):
            self.summary["rate_limits_read_result"] = "protocol_error"
            self.summary["last_error"] = "protocol_error"
            return

        self.summary["rate_limits_read_succeeded"] = True
        self.summary["rate_limits_read_result"] = "succeeded"
        self.summary["quota_window_count"], self.summary["quota_state"] = _count_quota_windows(result)
        self.summary["authenticated"] = True

    def _request(self, request_id: int, method: str, params: Any, stage: str) -> Any:
        if self.session is None:
            raise ProbeFailure("startup_failed", stage=stage)
        self.session.send_request(request_id, method, params)
        return self.session.wait_for_response(request_id, self.deadline, stage)

    def _send_notification(self, method: str) -> None:
        if self.session is None:
            raise ProbeFailure("startup_failed", stage=method)
        self.session.send_notification(method)

    def _remaining(self) -> float:
        return self.deadline - time.monotonic()

    def _top_level_error(self, failure: ProbeFailure) -> str:
        if failure.auth_hint:
            return "unauthenticated"
        if failure.stage == "initialize":
            return "initialize_failed"
        if failure.kind == "startup_failed":
            return "startup_failed"
        if failure.kind == "rpc_error":
            return "rpc_error"
        return failure.kind

    def _cleanup(self) -> None:
        process = self.process
        if process is None:
            self.summary["process_cleanup_succeeded"] = True
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

        self.summary["process_return_code"] = process.poll()
        self.summary["process_cleanup_succeeded"] = process.poll() is not None
        if self.session is not None:
            self.session.join_readers()

        for stream in (process.stdout, process.stderr):
            if stream is not None:
                try:
                    stream.close()
                except (OSError, ValueError):
                    pass

    @staticmethod
    def _terminate_process(process: subprocess.Popen[bytes]) -> None:
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

    def _copy_session_diagnostics(self) -> None:
        if self.session is None:
            return
        self.summary["malformed_json_count"] = self.session.malformed_json_count
        self.summary["stderr_observed"] = self.session.stderr_observed
        self.summary["eof_observed"] = self.session.eof_observed
        self.summary["notification_count"] = self.session.notification_count
        self.summary["ignored_message_count"] = self.session.ignored_message_count

    def _exit_code(self) -> int:
        if self.summary["success"]:
            return 0
        values = {
            "startup_failed": 2,
            "initialize_failed": 3,
            "unauthenticated": 4,
            "rate_limits_rpc_error": 5,
            "protocol_error": 6,
            "malformed_json": 6,
            "eof": 7,
            "timeout": 8,
            "interrupted": 130,
        }
        return values.get(self.summary["last_error"], 1)


def _safe_string(value: Any) -> Optional[str]:
    return value if isinstance(value, str) and value else None


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Probe Codex App Server over stdin/stdout JSONL.")
    parser.add_argument("--codex-bin", default="codex", help="Codex executable; default: codex")
    parser.add_argument(
        "--probe-account-read",
        action="store_true",
        help="also probe account/read; its unsupported result is recorded and does not stop the rate-limit probe",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=DEFAULT_TIMEOUT_SECONDS,
        help="total probe timeout in seconds; default: 30",
    )
    parser.add_argument(
        "--codex-source",
        help="optional provenance label, for example an npm registry or archive URL",
    )
    parser.add_argument(
        "--codex-revision",
        help="optional Codex commit or tag recorded with this probe",
    )
    parser.add_argument(
        "--codex-arg",
        action="append",
        default=[],
        help="extra executable argument, intended only for the local fake process",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be greater than zero")

    probe = P0Probe(args)
    exit_code = probe.run()
    print(json.dumps(probe.summary, ensure_ascii=False, sort_keys=True))
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
