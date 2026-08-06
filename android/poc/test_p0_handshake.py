#!/usr/bin/env python3
"""Offline regression test for the P0 account/read request envelope."""

import json
import subprocess
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PROBE = ROOT / "android" / "poc" / "p0_handshake.py"
FAKE = ROOT / "android" / "poc" / "fake_codex.py"


class P0HandshakeTests(unittest.TestCase):
    def test_account_read_sends_refresh_token_false(self) -> None:
        completed = subprocess.run(
            [
                sys.executable,
                str(PROBE),
                "--codex-bin",
                sys.executable,
                "--codex-arg",
                str(FAKE),
                "--probe-account-read",
                "--timeout",
                "10",
            ],
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
        summary = json.loads(completed.stdout)
        self.assertEqual(summary["account_read_result"], "unsupported")
        self.assertEqual(summary["account_read_rpc_code"], -32601)
        self.assertTrue(summary["rate_limits_read_succeeded"])
        self.assertTrue(summary["success"])


if __name__ == "__main__":
    unittest.main()
