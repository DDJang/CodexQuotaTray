#!/usr/bin/env python3
"""Offline-only fake for p0_handshake.py; never use this as P0 evidence."""

import json
import sys


def write(value):
    sys.stdout.write(json.dumps(value, separators=(",", ":")) + "\n")
    sys.stdout.flush()


if "--version" in sys.argv:
    print("codex-cli 0.0.0-p0-fake")
    raise SystemExit(0)

sys.stderr.write("fake upstream stderr\n")
sys.stderr.flush()
initialized = False
emit_malformed = "--malformed" in sys.argv

for line in sys.stdin:
    try:
        request = json.loads(line)
    except json.JSONDecodeError:
        continue

    if request.get("method") == "initialized":
        initialized = True
        continue

    request_id = request.get("id")
    method = request.get("method")
    if not isinstance(request_id, int):
        continue

    if method == "initialize":
        write(
            {
                "id": request_id,
                "result": {
                    "userAgent": "codex-cli/0.0.0-p0-fake",
                    "platformFamily": "fake",
                    "platformOs": "fake",
                },
            }
        )
    elif method == "account/read":
        params = request.get("params")
        valid_params = (
            isinstance(params, dict)
            and set(params) == {"refreshToken"}
            and type(params.get("refreshToken")) is bool
            and params["refreshToken"] is False
        )
        if valid_params:
            write({"id": request_id, "error": {"code": -32601, "message": "method not found"}})
        else:
            write({"id": request_id, "error": {"code": -32600, "message": "invalid params"}})
    elif method == "account/rateLimits/read" and initialized:
        if emit_malformed:
            sys.stdout.write("{ not-json\n")
            sys.stdout.flush()
        write(
            {
                "id": request_id,
                "result": {
                    "rateLimits": {
                        "primary": {"usedPercent": 28, "windowDurationMins": 300},
                        "secondary": {"usedPercent": 43, "windowDurationMins": 10080},
                    }
                },
            }
        )
    else:
        write({"id": request_id, "error": {"code": -32601, "message": "method not found"}})
