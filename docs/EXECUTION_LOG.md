# Execution Log

## Current state

- P0 complete.
- P1-A JSON-RPC reliable communication layer complete.
- P1-B App Server process supervisor complete.
- P1-C quota/application state reducer complete.
- P1-D1 pure refresh coordinator complete and ready to commit.
- 45 tests passing.
- No tray UI implemented.
- MVP remains read-only.

## Next milestone

Connect supervisor/RPC events and the refresh coordinator to the state store in a long-running runtime adapter.

## Completed milestones

- P0 protocol feasibility and documentation.
- P1-A reliable JSON-RPC transport.
- P1-B App Server process supervisor.
- P1-C quota/application state reducer.
- P1-D1 pure refresh coordinator.

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

## 2026-07-15 — P1-C quota/application state reducer

### Completed

- Added a deterministic reducer for process, authentication, data freshness, quota, timestamps, and anonymous warnings.
- Added a thread-safe in-memory store that returns owned normalized snapshots.
- Preserved the last valid quota through timeout, RPC, protocol, incomplete-data, and transport failures.
- Added explicit 15-minute stale transitions without replacing missing values with zero or 100.
- Cleared old quota only for explicit unauthenticated or non-ChatGPT account modes.
- Made sparse patch merging return an outcome and reject ambiguous multi-bucket patches without mutation.

### Files

- `src/state.rs`
- `src/protocol.rs`
- `src/lib.rs`
- `tests/state_reducer.rs`
- `tests/quota_parser.rs`
- `docs/TECH_DESIGN.md`
- `docs/API_CONTRACT.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test`: passed, 36 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.

### Remaining risks

- Runtime supervisor and RPC events are not yet orchestrated through the store.
- Refresh deduplication, ten-second minimum interval, ten-minute fallback, and notification-driven refresh are not implemented.
- Disk cache and long-duration soak remain outstanding.

### Next task

Implement the refresh coordinator with one in-flight request, minimum refresh spacing, fallback scheduling, failure projection, and offline tests.

## 2026-07-15 — P1-D1 pure refresh coordinator

### Completed

- Added a deterministic refresh scheduler with monotonic request IDs and one in-flight request.
- Coalesced concurrent startup, manual, notification, resume, network, card-open, and fallback triggers.
- Enforced a ten-second minimum interval, fifteen-second deadline, and ten-minute fallback cadence.
- Preserved the highest-priority pending reason and safely ignored unknown or duplicate completions.
- Added a virtual 24-hour scheduling replay with 86,400 ticks and no overlapping request.

### Files

- `src/refresh.rs`
- `src/lib.rs`
- `tests/refresh_coordinator.rs`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test`: passed, 45 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.

### Remaining risks

- The coordinator is pure scheduling logic; it does not yet execute RPC calls.
- Supervisor/RPC events are not yet projected into the state store by a long-running runtime.
- The virtual 24-hour test does not replace the required real process soak.

### Next task

Implement the runtime adapter that performs read-only refreshes, consumes sparse notifications, updates the store, and survives supervised process generations.
