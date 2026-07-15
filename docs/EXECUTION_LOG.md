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

## 2026-07-15 — P1-D2 long-running read-only runtime adapter

### Completed

- Connected supervised App Server generations to the JSON-RPC client, refresh coordinator, and normalized state store in one background runtime.
- Repeated the initialize/initialized handshake on every process generation without replaying pending wire requests across generations.
- Issued `account/read` and `account/rateLimits/read` concurrently and correlated deliberately out-of-order responses by request ID.
- Applied sparse rate-limit notifications without clearing metadata, then scheduled a rate-limited authoritative full read.
- Projected process, refresh, authentication, quota, timeout, protocol, and transport outcomes through the reducer while retaining the last valid snapshot.
- Added idempotent runtime shutdown and a privacy-safe aggregate report.
- Added an offline stdio fake App Server covering startup, refresh coalescing, sparse notification/full-read convergence, process recovery, and clean shutdown.

### Files

- `src/runtime.rs`
- `src/lib.rs`
- `tests/runtime_integration.rs`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --all -- --check`: passed.
- `cargo check --all-targets`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 50 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.
- Runtime integration suite was also run twice consecutively after timing fixes; both runs passed.

### Remaining risks

- The default CLI still uses the finite P0 probe path; the long-running runtime is a library service awaiting a Windows host adapter.
- Runtime CLI version detection and schema mismatch presentation are not implemented.
- The required real Codex process soak and resource/orphan measurements remain outstanding.
- No disk cache is present; adding one requires an explicit minimal-field privacy review.

### Next task

Finish P1 with version detection, the cache/settings decision, and a real-process soak before starting any tray UI work.

## 2026-07-15 — P1-D3 schema compatibility and real-runtime soak harness

### Completed

- Read the pinned version from `schemas/CODEX_VERSION` at compile time instead of duplicating it in runtime code.
- Extracted only a version token from the initialized App Server user-agent and projected match, mismatch, or unreported into normalized state.
- Kept schema mismatch best-effort and read-only while attaching an explicit anonymous warning; no restart loop is triggered by a version difference alone.
- Stored only the parsed runtime version in quota provenance; the full user-agent is not retained by the state store.
- Added a finite soak example that reports only process/data state, window count, version compatibility, booleans, and aggregate counters.
- Ran a 90-second real App Server soak against codex-cli 0.137.0. It remained generation 0/fresh with one normalized window and exited cleanly.

### Dependency decision

- No production dependency was added. Version parsing, finite scheduling, and reporting use the standard library plus existing project types.
- This avoids binary-size and runtime impact and is sufficient because only the stable CLI version token is required, not general semantic-version ordering.

### Files

- `src/compatibility.rs`
- `src/lib.rs`
- `src/main.rs`
- `src/runtime.rs`
- `src/state.rs`
- `tests/runtime_integration.rs`
- `tests/state_reducer.rs`
- `examples/runtime_soak.rs`
- `README.md`
- `docs/API_CONTRACT.md`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 54 tests.
- `cargo run --example runtime_soak -- --seconds 90 --sample-seconds 15`: passed.
- Real soak report: 1 refresh success, 0 failures, 0 notifications, 0 restarts, 0 forced terminations, 0 protocol diagnostics.
- Corrected WMI orphan query: no remaining `node.exe`/`codex.exe` process with `app-server --stdio`.

### Remaining risks

- The 90-second real run is a smoke, not the 24-hour P1 exit gate.
- No live update notification was observed; notification behavior remains covered by the offline process fixture.
- Disk quota cache remains disabled pending a minimal-field privacy and corruption-recovery design.

### Next task

Design and implement the minimal non-sensitive cache/settings boundary, then execute the longest practical real-process resource soak before P2.

## 2026-07-15 — P1-E privacy-minimized settings and quota cache

### Completed

- Added separate settings and quota-cache stores under a caller-selected directory, with `%LOCALAPPDATA%\CodexQuotaTray` discovery for the Windows host.
- Defined versioned settings defaults for startup, display mode, time format, cache preference, fallback refresh, network recovery, and notification toggles.
- Persisted only used percentage, source slot, duration, reset timestamp, last-success timestamp, and parsed CLI version for quota cache.
- Explicitly excluded limit IDs/names, plan/auth mode, account/email/token data, protocol warnings, and raw responses from the cache schema.
- Bounded persistence files to 64 KiB and cache windows to 32; rejected invalid versions, percentages, durations, timestamps, settings, malformed JSON, and oversized input.
- Added temporary/backup replacement, backup recovery, and idempotent primary/backup/temporary cache clearing.
- Wired the optional cache into runtime startup and successful live/notification updates. Restored data is always stale/auth-unknown until live reads replace it.
- Made cache load/write failures non-fatal and privacy-safe through an anonymous warning and aggregate counter.

### Dependency decision

- No new production dependency was added. Existing Serde/serde_json and the standard filesystem APIs cover the small JSON files.
- Expected release-size impact is only project code; runtime work occurs only at startup, successful low-frequency refresh, settings save, or explicit clear.
- SQLite was rejected for this bounded schema because it would add binary/runtime complexity without transactional or query benefits needed by the MVP.

### Files

- `src/persistence.rs`
- `src/lib.rs`
- `src/runtime.rs`
- `src/state.rs`
- `tests/persistence.rs`
- `tests/runtime_integration.rs`
- `examples/runtime_soak.rs`
- `README.md`
- `docs/API_CONTRACT.md`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 62 tests.
- `git diff --check`: passed; only Git LF/CRLF conversion notices were emitted.
- Cache privacy regression asserts the serialized file contains none of the forbidden identity/authentication field names.

### Remaining risks

- Actual Windows startup registration is not implemented; only the persisted preference exists.
- The Windows host must opt into the quota cache according to settings; the core runtime does not silently write LocalAppData by default.
- Standard-library replacement is crash-recoverable through a backup but does not fsync the containing directory.
- The 24-hour real-process/resource soak remains the only P1 exit gate not met.

### Next task

Run the finite 24-hour real-process soak with external resource samples and an orphan check; do not start P2 before the result is recorded.

## 2026-07-15 — P2-A platform-neutral tray projection and alert reducer

### Completed

- Added a pure `AppState` projection for tray severity, tooltip, account/status headline, quota rows, progress percentages, reset countdowns, last update, and reset-credit-unavailable text.
- Derived quota names from duration/official normalized names, never from primary/secondary slot assumptions.
- Kept missing windows/reset times unavailable instead of presenting 100% or zero.
- Added distinct normal, caution, critical, exhausted, refreshing, and offline semantic icon states.
- Added a pure in-memory alert tracker for 20%, 5%, exhausted, and recovered events.
- Suppressed alerts on the initial observation, deduplicated each threshold per window/cycle, emitted only the most severe event on large drops, and rearmed after a confirmed cycle recovery.
- Continued the independent finite 24-hour release soak; at the five-minute check the main process remained alive at about 5.8 MB working set, 0.56 cumulative CPU seconds, and empty stderr.

### Dependency decision

- No dependency was added for projection or alert logic; existing normalized types and Chrono cover local time presentation.
- Official Microsoft `windows-rs` documentation was reviewed for the next adapter. It describes `windows-sys` as zero-overhead raw bindings and the `windows` crate as the safer Win32/COM/WinRT projection, both namespace-feature-gated. The current official API docs report 0.62.2.
- The Win32 dependency will be added only with the exact Shell/WindowsAndMessaging/GDI/Registry features needed by the host; no webview/browser feature is permitted.

### Files

- `src/ui_model.rs`
- `src/alerts.rs`
- `src/lib.rs`
- `tests/ui_model.rs`
- `tests/alerts.rs`
- `docs/TECH_DESIGN.md`
- `docs/ROADMAP.md`

### Verification

- `cargo test --test ui_model --test alerts`: passed, 11 tests.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- Independent soak status: alive after 307 seconds; runtime working set 5,849,088 bytes; stderr 0 bytes.

### Remaining risks

- No Win32 tray/window/balloon adapter exists yet.
- Alert deduplication is currently in memory only; persisted notification-cycle state remains to be designed if restart deduplication is required for MVP.
- The wall-clock 24-hour P1 gate is still running and P1 is not marked complete.

### Next task

Add the smallest official Windows binding feature set and implement a demo-mode native tray host against the tested projection, while the independent soak continues.

## 2026-07-15 — P2-B native read-only Windows tray host

### Completed

- Added a Windows-only `windows` 0.62.2 adapter using Shell, WindowsAndMessaging, GDI, Registry, Foundation, LibraryLoader, Security, and Threading namespace features.
- Added a native tray icon with six semantic icon states, a normalized tooltip, a dark read-only quota card, dynamic Chinese duration names, progress bars, reset countdowns, last-update state, and reset-credit-unavailable text.
- Connected the host only to `AppState`/`TrayView`, `QuotaRuntime::snapshot`, and `QuotaRuntime::request_refresh`; the UI has no wire JSON or account identity types.
- Added deterministic debug/demo state changes for safe visual work, while release builds default to the real read-only P1 runtime.
- Added a standard Win32 context menu for refresh, official Usage navigation, non-sensitive cache, quota reminders, start-with-Windows, cache clearing, and exit.
- Wired Windows session shutdown, menu exit, and `--shutdown-existing` into the normal runtime/App Server cleanup path.
- Made runtime cache enable/disable effective immediately through an atomic gate; disabling clears the cache and prevents subsequent runtime write-back.
- Localized quota names for card and notification text without changing the protocol/domain parser's stable English debug names.
- Added an aggregate non-sensitive window title plus `Enter`/`R`, `U`, `F10`, and `Esc` keyboard paths. Standard Win32 popup menu commands remain accessible even though individual self-drawn rows are not separate UIA nodes.

### Dependency decision

- `windows` is target-gated to Windows and uses only the named Win32 features required by this host. The official Microsoft projection avoids hand-written FFI while preserving a native process with no browser runtime.
- The optimized stripped/thin-LTO GUI is 931,328 bytes, so the dependency does not threaten the installer-size target. Idle UI work is timer-driven every 5 seconds and only reads an in-memory snapshot; it does not alter the 10-minute network fallback.
- A browser UI, WebView, Electron, Chromium, SQLite, async runtime, and image/icon asset dependency were all unnecessary.

### Files

- `Cargo.toml`, `Cargo.lock`
- `src/bin/codex-quota-tray-gui.rs`
- `src/windows_tray.rs`
- `src/lib.rs`
- `src/ui_model.rs`, `src/alerts.rs`
- `src/persistence.rs`
- `tests/ui_model.rs`, `tests/persistence.rs`
- `README.md`
- `docs/TECH_DESIGN.md`, `docs/ROADMAP.md`, `docs/EXECUTION_LOG.md`

### Verification

- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 74 tests total.
- `git diff --check`: passed before documentation update; rerun at commit gate.
- Windows 11 Home Chinese x64 10.0.26200 manual smoke: demo card rendered correctly, manual refresh changed normalized fixture state, context-menu checks were correct, and menu Exit removed the window.
- Real release host started the P1 runtime and exited through `--shutdown-existing`; process inspection found no additional tray or App Server process after cleanup. The independent P1 soak tree remained intact.
- Ten-second stable main-process sample: working set 10,145,792 → 10,096,640 bytes, CPU delta 0 seconds, 6 threads, 139 handles. This excludes the separately measured App Server process tree.

### Remaining risks

- P1's independent 24-hour wall-clock soak is still running, so P1/P2 gates are not marked complete.
- Only Windows 11 build 26200 has a manual host smoke; Windows 10, multiple monitors, and additional DPI combinations remain release-test items.
- Self-drawn quota rows do not expose individual UIA nodes. The aggregate title, keyboard shortcuts, and standard menu are the current minimum accessible path; native child controls or a UIA provider remain a P4 decision.
- The official Usage menu item was visually verified but deliberately not activated during smoke, so its external navigation remains a release checklist item.

### Next task

Add P3 card-open/resume refresh adapters and quiet-time notification behavior without changing the P1 refresh bounds, while the 24-hour soak continues.

## 2026-07-15 — P3 host events and quiet-time notifications

### Completed

- Added a pure `HostEvent` mapping for card-open, session-resume, network-offline, and network-restored inputs.
- Routed card-open through `RefreshReason::CardOpened`, `PBT_APMRESUMEAUTOMATIC` through `Resume`, and IP Helper InternetAccess callbacks through `NetworkRestored`.
- Kept network-offline events passive; existing transport failures preserve the last snapshot and the 10-minute fallback remains authoritative.
- Registered the Windows connectivity callback with only an HWND value as context, posted work back to the UI thread, and canceled the notification handle on every normal exit path.
- Changed second-instance activation to post a card-open message to the owning UI thread instead of directly showing the window, preserving single-instance event semantics.
- Added `NIIF_RESPECT_QUIET_TIME | NIIF_NOSOUND` to native quota notifications.
- Added an offline burst test proving repeated network-restored events coalesce behind the 10-second minimum interval rather than producing parallel reads.

### Dependency decision

- Reused the existing Windows-only `windows` crate and added only its IP Helper and WinSock metadata features. The implementation calls the operating system connectivity hint API; it performs no web request, DNS probe, browser access, or cookie read.
- No async runtime, network client, service, or polling thread was added. The callback posts one UI message only for InternetAccess; P1 remains the sole owner of request throttling and network I/O.

### Files

- `Cargo.toml`
- `src/host_events.rs`, `src/lib.rs`
- `src/windows_tray.rs`
- `tests/host_events.rs`, `tests/refresh_coordinator.rs`
- `README.md`
- `docs/TECH_DESIGN.md`, `docs/ROADMAP.md`, `docs/EXECUTION_LOG.md`

### Verification

- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 77 tests total.
- P3 stripped/thin-LTO release GUI: 932,352 bytes, only 1,024 bytes above the P2 host baseline.
- Windows 11 smoke: the host registered the connectivity callback, a second launch retained one window/instance, and Alt+F4 returned from callback cancellation with no residual GUI process.
- P1 soak at 3,600 seconds: generation 0, process Ready, data Fresh, one normalized window, schema match 0.137.0, last success present, 0 warnings; stderr 0 bytes. Harness working set was 5,533,696 bytes at the later sample.

### Remaining risks

- A real Wi-Fi disconnect/reconnect and sleep/resume cycle was not forced because changing the user's live system connectivity/power state is outside safe automated smoke scope; pure mappings and coordinator behavior are tested offline.
- Windows connectivity callback registration failure is intentionally non-fatal because the 10-minute fallback must continue to work on unsupported/restricted systems.
- P1's 24-hour soak and Windows 10 host smoke remain open release gates.

### Next task

Create a reproducible P4 Windows package with dependency/license inventory, install/upgrade/uninstall scripts, start-with-Windows handling, and offline packaging tests; do not sign or publish it.
