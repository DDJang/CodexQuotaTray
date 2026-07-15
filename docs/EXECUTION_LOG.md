# Execution Log

## Current state

- P0 complete.
- P1-A JSON-RPC reliable communication layer complete.
- P1-B App Server process supervisor complete and ready to commit.
- 27 tests passing.
- No tray UI implemented.
- MVP remains read-only.

## Next milestone

Implement the quota state reducer and in-memory state store.

## Completed milestones

- P0 protocol feasibility and documentation.
- P1-A reliable JSON-RPC transport.
- P1-B App Server process supervisor.

## 2026-07-15 — P1-A reliable JSON-RPC transport

### Completed

- Added connection-local monotonic request IDs and a concurrent pending-request map.
- Matched out-of-order responses by ID and separated success, RPC error, notification, and diagnostic paths.
- Added per-request deadlines and deterministic failure of all pending requests on stdout EOF.
- Safely discarded unknown IDs, duplicate responses, unsupported ID types, malformed JSON, and invalid envelopes.
- Preserved the P0 read-only quota probe and moved its requests onto the reliable transport.

### Files

- `src/json_rpc.rs`
- `src/app_server.rs`
- `src/protocol.rs`
- `src/main.rs`
- `src/lib.rs`
- `tests/json_rpc_client.rs`
- `tests/quota_parser.rs`
- `docs/TECH_DESIGN.md`
- `docs/API_CONTRACT.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test`: passed, 17 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.

### Remaining risks

- No process restart or backoff exists yet.
- Shutdown is not yet idempotent.
- Process-level behavior is not yet tested with a fake executable.

### Next task

Implement P1-B App Server process supervision, bounded restart backoff, idempotent shutdown, and fake-process integration tests.

## 2026-07-15 — P1-B App Server process supervisor

### Completed

- Added a long-running supervisor thread that owns one App Server generation at a time.
- Added automatic recovery after nonzero exit, spawn failure, stdout/transport loss, or an explicit transport recovery request.
- Implemented capped exponential backoff from 1 to 30 seconds, bounded 0–20% jitter, and at most five restarts per five-minute window.
- Made both `AppServer::shutdown` and supervisor shutdown idempotent.
- Kept stderr continuously drained while retaining only a non-sensitive observed/not-observed flag.
- Migrated the P0 CLI probe to supervised connection generations without replaying requests across processes.
- Added a fake child-process harness covering graceful EOF, stderr flood, nonzero exit, restart exhaustion, missing executable, explicit recovery, and forced termination.

### Files

- `src/app_server.rs`
- `src/supervisor.rs`
- `src/main.rs`
- `src/lib.rs`
- `tests/app_server_supervisor.rs`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed after fixing one `collapsible_if` warning.
- `cargo test`: passed, 27 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.
- Read-only live smoke with `--watch-seconds 0`: passed with exit code 0 on codex-cli 0.137.0.
- Post-smoke process query found no remaining `app-server --stdio` process.

### Remaining risks

- App state still exists only as immediate CLI output; there is no reducer or stale/offline preservation yet.
- Restart events are not yet projected into user-facing process/data states.
- The required long-duration soak remains outstanding.

### Next task

Implement the pure quota/application state reducer, preserve the last valid snapshot across failures, and add state-transition tests.
