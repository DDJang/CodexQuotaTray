#!/usr/bin/env python3
"""Offline-only fake App Server for the P1 Bridge tests."""

import json
import sys


def _option(name, default):
    try:
        index = sys.argv.index(name)
    except ValueError:
        return default
    return sys.argv[index + 1] if index + 1 < len(sys.argv) else default


def _write(value):
    sys.stdout.write(json.dumps(value, separators=(",", ":")) + "\n")
    sys.stdout.flush()


if "--version" in sys.argv:
    print("codex-cli 0.0.0-p1-fake")
    raise SystemExit(0)

mode = _option("--mode", "happy")
sys.stderr.write("fake P1 upstream stderr\n")
sys.stderr.flush()
initialized = False

for line in sys.stdin:
    try:
        request = json.loads(line)
    except json.JSONDecodeError:
        continue
    if not isinstance(request, dict):
        continue

    method = request.get("method")
    if method == "initialized":
        initialized = True
        continue

    request_id = request.get("id")
    if type(request_id) is not int:
        continue

    if method == "initialize":
        _write(
            {
                "id": request_id,
                "result": {
                    "userAgent": "codex-cli/0.0.0-p1-fake",
                    "platformFamily": "fake",
                    "platformOs": "fake",
                },
            }
        )
        continue

    if method != "account/rateLimits/read" or not initialized:
        _write({"id": request_id, "error": {"code": -32601, "message": "method not found"}})
        continue

    if mode == "malformed":
        sys.stdout.write("{ malformed P1 JSON\n")
        sys.stdout.flush()

    if mode == "unavailable":
        result = {"rateLimits": {"primary": {}}}
    elif mode == "zero":
        result = {"rateLimits": {}}
    else:
        result = {
            "rateLimits": {
                "limitId": "[REDACTED-P1-LIMIT]",
                "limitName": "Codex",
                "planType": "plus",
                "primary": {"usedPercent": 28, "windowDurationMins": 300, "resetsAt": 1900000000},
                "secondary": {"usedPercent": 43, "windowDurationMins": 10080, "resetsAt": 1900500000},
            },
            "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                    {"id": "[REDACTED-P1-CREDIT]", "status": "available", "expiresAt": 1901000000},
                    {"id": "[REDACTED-P1-CREDIT-2]", "status": "available", "expiresAt": 1902000000},
                ],
            },
        }

    if mode == "sparse-before":
        _write(
            {
                "method": "account/rateLimits/updated",
                "params": {"rateLimits": {"primary": {"usedPercent": 31}}},
            }
        )

    _write({"id": request_id, "result": result})

    if mode == "sparse":
        _write(
            {
                "method": "account/rateLimits/updated",
                "params": {"rateLimits": {"primary": {"usedPercent": 31}}},
            }
        )
    if mode == "disconnect":
        raise SystemExit(0)
