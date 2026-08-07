#!/usr/bin/env python3
"""Offline P1 Bridge tests using only the Python standard library."""

import json
import sys
import threading
import time
import unittest
from pathlib import Path
from urllib.request import urlopen


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))

from bridge import BridgeHTTPServer, BridgeRuntime, merge_response, normalize_response  # noqa: E402


FAKE = Path(__file__).resolve().parent / "fake_upstream.py"


def _full_fixture():
    return json.loads((ROOT / "winui/tests/fixtures/rate_limits_reset_credits.json").read_text(encoding="utf-8"))


class BridgeTests(unittest.TestCase):
    def test_dynamic_windows_and_public_projection_do_not_expose_ids(self) -> None:
        fixture = json.loads(
            (ROOT / "winui/tests/fixtures/rate_limits_multi_bucket.json").read_text(encoding="utf-8")
        )
        normalized = normalize_response(fixture)
        self.assertEqual(normalized["windowCount"], 3)
        self.assertTrue(any(window["percentageReliable"] is False for window in normalized["windows"]))
        self.assertTrue(any(window["usedPercent"] == 100 for window in normalized["windows"]))
        self.assertNotIn("[REDACTED-A]", json.dumps(normalized))
        self.assertNotIn("limitId", json.dumps(normalized))

    def test_reset_credit_five_states(self) -> None:
        cases = [
            ({}, "Unavailable"),
            ({"rateLimitResetCredits": None}, "Unavailable"),
            ({"rateLimitResetCredits": {"availableCount": 0}}, "Empty"),
            (
                {"rateLimitResetCredits": {"availableCount": 2, "credits": [{"expiresAt": None}]}},
                "CountOnly",
            ),
            (
                {"rateLimitResetCredits": {"availableCount": 2, "credits": [{"expiresAt": 1900000000}]}},
                "PartialDetails",
            ),
            (
                {"rateLimitResetCredits": {"availableCount": 1, "credits": [{"expiresAt": 1900000000}]}},
                "CompleteDetails",
            ),
        ]
        for response, expected in cases:
            with self.subTest(expected=expected):
                self.assertEqual(normalize_response(response)["resetCredits"]["kind"], expected)

    def test_sparse_merge_preserves_omitted_windows_and_reset_credits(self) -> None:
        baseline = _full_fixture()
        patch = {"rateLimits": {"primary": {"usedPercent": 31}}}
        merged = merge_response(baseline, patch)
        self.assertIsNotNone(merged)
        normalized = normalize_response(merged)
        primary = next(window for window in normalized["windows"] if window["sourceSlot"] == "primary")
        self.assertEqual(primary["usedPercent"], 31)
        self.assertEqual(primary["windowDurationMinutes"], 300)
        self.assertEqual(primary["resetAtUtc"], "2030-03-17T17:46:40Z")
        self.assertEqual(normalized["resetCredits"]["kind"], "CompleteDetails")

    def test_http_status_and_sparse_notification(self) -> None:
        runtime = BridgeRuntime(
            codex_bin=sys.executable,
            codex_args=[str(FAKE), "--mode", "sparse-before"],
            timeout_seconds=2,
            refresh_seconds=60,
            retry_seconds=0.05,
            max_restarts=2,
            port=0,
        )
        server = BridgeHTTPServer(("127.0.0.1", 0), runtime)
        server_thread = threading.Thread(target=server.serve_forever, daemon=True)
        server_thread.start()
        runtime.start()
        try:
            self.assertTrue(
                _wait_for(
                    lambda: (
                        runtime.snapshot()["connection"]["state"] == "fresh"
                        and runtime.snapshot()["quota"]["windows"][0]["usedPercent"] == 31
                    )
                )
            )
            with urlopen(f"http://127.0.0.1:{server.server_port}/v1/status", timeout=2) as response:
                payload = json.loads(response.read().decode("utf-8"))
            self.assertEqual(payload["schemaVersion"], 1)
            self.assertEqual(payload["connection"]["authenticated"], True)
            self.assertEqual(payload["quota"]["resetCredits"]["kind"], "CompleteDetails")
            self.assertNotIn("[REDACTED-P1-LIMIT]", json.dumps(payload))
            self.assertNotIn("[REDACTED-P1-CREDIT]", json.dumps(payload))
        finally:
            server.shutdown()
            server.server_close()
            runtime.stop()
            self.assertTrue(runtime.snapshot()["diagnostics"]["processCleanupSucceeded"])

    def test_disconnected_process_is_restarted(self) -> None:
        runtime = BridgeRuntime(
            codex_bin=sys.executable,
            codex_args=[str(FAKE), "--mode", "disconnect"],
            timeout_seconds=1,
            refresh_seconds=0.05,
            retry_seconds=0.05,
            max_restarts=2,
            port=0,
        )
        runtime.start()
        try:
            self.assertTrue(_wait_for(lambda: runtime.snapshot()["diagnostics"]["startCount"] >= 2, 4))
        finally:
            runtime.stop()
        self.assertTrue(runtime.snapshot()["diagnostics"]["processCleanupSucceeded"])


def _wait_for(predicate, timeout=3.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


if __name__ == "__main__":
    unittest.main()
