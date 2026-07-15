# Execution Log

## Current state

- P0 complete.
- P1-A JSON-RPC reliable communication layer complete and ready to commit.
- 17 tests passing.
- No tray UI implemented.
- MVP remains read-only.

## Next milestone

Implement the App Server process supervisor and process-level integration tests.

## Completed milestones

- P0 protocol feasibility and documentation.
- P1-A reliable JSON-RPC transport.

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
