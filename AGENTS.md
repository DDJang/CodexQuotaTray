# CodexQuotaTray Development Instructions

## Project goal

Build a lightweight Windows system-tray application that displays Codex rate-limit windows, reset times, and available rate-limit reset credits.

The product requirements are defined in `docs/PRD.md`.

## Development principles

* Target Windows 10 and Windows 11.
* Prefer low idle CPU usage and low memory usage.
* Do not use Electron or embed a browser runtime.
* Do not scrape the Codex or ChatGPT web interface.
* Do not read browser cookies.
* Do not store authentication tokens in application logs or plaintext configuration files.
* Treat all App Server response fields as potentially absent unless the generated schema marks them as required.
* Do not assume `primary` always represents five hours.
* Do not assume `secondary` always represents seven days.
* Use `windowDurationMins`, `limitId`, and `limitName` to identify quota windows.
* Treat `rateLimitResetCredits.availableCount` as authoritative.
* MVP must remain read-only and must not consume a reset credit.

## Architecture rules

Keep these responsibilities separate:

1. Codex App Server process management.
2. JSON-RPC transport.
3. Protocol response types.
4. Quota parsing and domain models.
5. Application state.
6. Windows tray and popup UI.
7. Settings and cache persistence.
8. Notifications.

The UI must not parse raw JSON-RPC responses directly.

## Workflow

Before implementing a milestone:

1. Read `docs/PRD.md`.
2. Read `docs/TECH_DESIGN.md`.
3. Read the relevant milestone in `docs/ROADMAP.md`.
4. Inspect the existing implementation and tests.
5. Propose a brief implementation plan.
6. Make the smallest coherent change.
7. Run formatting, static checks, and relevant tests.
8. Report changed files, commands executed, and any unresolved risks.

## Testing requirements

* Add unit tests for quota parsing and state transitions.
* Use anonymized JSON fixtures for protocol responses.
* Do not require a real Codex account for unit tests.
* Add regression tests for every parser bug.
* Test missing fields, null values, unknown quota windows, and malformed responses.
* Do not mark a task complete when tests are failing.

## Error handling

* Do not panic on malformed server responses.
* Do not silently replace unknown or missing values with zero.
* Preserve the last valid quota state when a refresh fails.
* Clearly distinguish fresh, refreshing, stale, offline, unauthenticated, and unavailable states.
* Use bounded retries with backoff when restarting App Server.

## Logging and privacy

Never log:

* Access tokens.
* Refresh tokens.
* Browser cookies.
* User email addresses.
* Full account identifiers.
* Full reset-credit identifiers.
* Raw authentication responses.

Keep diagnostic logs concise and redact potentially identifying values.

## Scope control

Do not implement features outside the current milestone without an explicit request.

Do not add a new production dependency without explaining:

* Why it is needed.
* Its maintenance status.
* Its binary-size impact.
* Its runtime impact.
* Whether the same result can be achieved with an existing dependency.

## Completion report

At the end of each task, report:

* What was implemented.
* Files changed.
* Tests and checks run.
* Results of those checks.
* Remaining limitations.
* Recommended next task.
