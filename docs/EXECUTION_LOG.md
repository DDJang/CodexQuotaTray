# Execution Log

## 2026-07-19 — WinUI Phase 3 runtime baseline

- Added the single `QuotaRuntimeService`, refresh policy/backoff/stale calculation, sparse App Server notification merge, camelCase settings/cache/alert persistence, 50/20/10 threshold reducer, settings window, preview startup registration, tray quick actions, resume and IP Helper network events.
- Preview data remains isolated in `%LOCALAPPDATA%\CodexQuotaTray-WinUI-Preview`; explicit production import validates and copies supported documents without modifying the Rust source directory.
- .NET Release build completed with zero warnings. C# tests: 69 total, 68 passed, one opt-in real-account resource smoke skipped. Rust regression: 101 passed. Formatting, strict Clippy and `git diff --check` passed.
- A four-second Release `--demo` sample observed approximately 176.89 MiB working set, 857 handles, 29 threads and 0.89 CPU seconds. The launched process was stopped; the immediate process-count check raced with shutdown, and a subsequent check confirmed no remaining preview process.
- Successful real-account 50-refresh and five-minute Phase 3 soak remain blocked because the installed `0.145.0-alpha.18` App Server returns `-32600` for `account/rateLimits/read`; no repeated real-account reads were attempted.

## 2026-07-17 — Popup header icon removal

- Removed the decorative application icon from the popup header after manual DPI checks showed
  that its Win32 `HICON` rasterization remained visibly soft.
- Reclaimed the icon column for the title and status text, aligning both with the quota panels.
- Kept the embedded PE icon, window icons, notification-area icon, and flat information glyph.

## 2026-07-17 — 0.1.4 tray icon and visibility root-cause repair

- Loaded the embedded PE icon resources synchronously before class registration and removed all
  stock `IDI_QUESTION`/`IDI_INFORMATION`/`IDI_APPLICATION` fallback paths. The first `NIM_ADD`
  carries the product HICON and is followed by `NIM_SETVERSION` 4; `TaskbarCreated` alone can
  trigger a delete/re-add cycle.
- Moved shell callbacks to an independent `HWND_MESSAGE`; only `WM_LBUTTONUP` (the low word of
  the Version 4 callback) posts one `WM_APP_TOGGLE_WINDOW`. Visibility now comes only from the
  UI-thread `desired_visible` state, without focus-loss hiding, timers, or debounce suppression.
- Debug logs use a monotonic sequence and elapsed milliseconds. In the Codex desktop runner,
  `Shell_NotifyIconW(NIM_ADD)` returned `E_FAIL` (2147500037), so a real Explorer notification
  smoke was unavailable; the release path reports the error instead of showing a placeholder.

## 2026-07-17 — DPI-native icon rendering correction

- Confirmed the remaining blur was caused by fixed 32px/16px HICON handles being enlarged by
  `DrawIconEx` and the window shell at 125%–200% DPI; the PE ICO already contained the required
  16/20/24/32/40/48/64/128/256 frames.
- Added a DPI icon set separate from the registered class icons. The UI icon now uses the same
  physical rectangle size as `CardLayout`, while window and tray icons select the nearest larger
  embedded frame using per-DPI system metrics.
- `WM_DPICHANGED` loads all replacement handles first, updates the tray/window references, then
  releases the old dynamic set. A failed reload keeps the previous valid product icons.

## 2026-07-16 — 0.1.4 light UI and tray toggle

- Removed the rounded status badge; status remains as semantic text with normal, caution, critical, refreshing, and offline colors.
- Switched the opaque card to the light palette (`#F5F7FA` root, white quota panels, dark primary text, mint primary action) while preserving ClearType Natural and Per-Monitor V2.
- Anchored the card to the active monitor `rcWork` lower-right corner above the taskbar, with clamping for negative coordinates and small work areas.
- The final visibility path is handled by the independent tray message window and queued
  `WM_APP_TOGGLE_WINDOW`; focus loss is not used to hide the card.

## 2026-07-17 — compact quota card presentation

- Merged the update label and last-update timestamp into one title-area status line, with explicit refreshing and retry wording.
- Replaced the single quota reset sentence with separate countdown and local-date rows, normalized `Plus` casing, and shortened the reset-credit notice to a low-weight Chinese information strip.
- Recomputed card height from 0–3 quota panels and optional settings warnings; buttons now follow the information strip without a reserved blank footer.
- Updated button labels and removed the R/U shortcut hints and key commands; Enter, Tab/arrow focus, Space, F10, and Esc remain unchanged.

## 2026-07-17 — Inno Setup release installer

- Added `installer/CodexQuotaTray.iss` for a per-user Inno Setup 7 installer with embedded application icon, Start Menu shortcut, default HKCU autostart task, upgrade shutdown, and generated uninstaller.
- Added `scripts/package-inno.ps1` to build the locked x64 release and invoke `ISCC.exe`; it fails with an actionable message when Inno Setup is not installed.
- The formal uninstaller preserves `%LOCALAPPDATA%\CodexQuotaTray` user data while removing the installed executable, shortcut, and autostart value.

## Current state

- P0 complete.
- P1 service core complete; the independent real-process soak ran for 76,251 seconds (21 h 11 min) before the user explicitly ended the remaining monitoring window.
- P2 native Win32 tray host and P3 host-event/quiet-time integration implemented.
- P4 reproducible per-user ZIP packaging, isolated install/upgrade/uninstall smoke, privacy notice, dependency inventory, and unsigned release strategy implemented locally.
- 103 tests pass at the final P4/MVP gate, including the offline icon-event and desired-visibility
  checks.
- MVP remains read-only.

## Next milestone

The local read-only MVP baseline is complete. Remaining work is public-release validation: Windows 10, broader DPI/multi-monitor coverage, signing/provenance, and the separate seven-day quality soak.

## 2026-07-16 — 0.1.3 Per-Monitor V2 clarity correction

- Diagnosed the packaged 0.1.2 process with `GetProcessDpiAwareness`: it reported `PROCESS_DPI_UNAWARE`, and `mt.exe` confirmed that standard `RT_MANIFEST #1` was absent.
- Corrected the resource type to numeric 24, added an early Per-Monitor V2 runtime assertion, and based the initial card layout on the cursor monitor's effective DPI.
- Converted GDI font sizes from points to physical pixels and selected ClearType Natural quality; opaque pixel-for-pixel double buffering remains unchanged.
- Release validation now requires both an embedded manifest and observed Per-Monitor-aware process/window DPI rather than trusting the source XML alone.

## Completed milestones

## 2026-07-16 — 0.1.2 crisp rendering and rounded icon correction

### Completed

- Removed whole-window layered alpha after Windows visual inspection showed that it degraded GDI ClearType and produced transient composition artifacts during invalidation.
- Kept the opaque double-buffered dark card, PerMonitorV2 layout, DWM dark-mode corners and shadows, and all existing interactions.
- Preserved the supplied 24-bit artwork as `app-icon-source.png`; the generator now creates a 32-bit antialiased rounded-square preview and nine-frame ICO with transparent corners.
- Kept protocol, refresh, privacy, cache, notification, and reset-credit boundaries unchanged.

### Dependency decision

- No dependency was added. Rounded Alpha masking and ICO validation use the existing build-time PowerShell/System.Drawing path; runtime remains native Win32/GDI.

### Verification

- The generator enforces transparent corners, an opaque center, and exact 16/20/24/32/40/48/64/128/256 frame dimensions.
- See the final task report for formatting, Clippy, tests, package smoke, embedded-icon inspection, and Windows DPI visual results.

## 2026-07-16 — 0.1.1 native visual polish and embedded icon

### Completed

- Kept the read-only Win32 host and added a dark, DPI-aware quota instrument layout with rounded quota surfaces, mint status accents, dynamic height, shared paint/hit-test geometry, mouse interaction states, and keyboard focus navigation.
- Added official Windows 11 Desktop Acrylic/rounded-corner attributes with a non-fatal Windows 10 solid fallback.
- Preserved the supplied 1254×1254 icon as source, generated a nine-size ICO, and embedded the icon plus PerMonitorV2/asInvoker manifest into the executable.
- Kept protocol, refresh, privacy, cache, notification, and reset-credit boundaries unchanged.

### Dependency decision

- Added `embed-resource` 3.0.11 as a build-only dependency. It locates the MSVC resource compiler and links the checked-in icon/manifest; it has no runtime code, background activity, or framework deployment. The existing `windows` dependency only gained feature-gated DWM, HiDPI, and mouse-input bindings.
- WinUI 3 was rejected for this iteration because the Windows App SDK bootstrap/runtime and host rewrite would increase packaging and compatibility risk without improving the existing tray lifecycle.

### Verification

- Offline layout tests cover 0–3 rows at 96/120/144/192 DPI, shared button hit regions, semantic tray icon shapes, and DWM failure fallback.
- See the final task report for formatting, Clippy, test, package, icon-resource, and Windows smoke results.

- P0 protocol feasibility and documentation.
- P1-A reliable JSON-RPC transport.
- P1-B App Server process supervisor.
- P1-C quota/application state reducer.
- P1-D1 pure refresh coordinator.
- P1-D2 runtime adapter, P1-D3 schema compatibility, and P1-E privacy-minimized persistence.
- P2 read-only native Windows tray host.
- P3 host events and quiet-time notifications.
- P4 reproducible package and local MVP validation.

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
- Routed card-open through `RefreshReason::CardOpened` only when data is missing or at least 60 seconds old, `PBT_APMRESUMEAUTOMATIC` through `Resume`, and IP Helper InternetAccess callbacks through `NetworkRestored`.
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

## 2026-07-15 — P4 reproducible package and release baseline

### Completed

- Added a locked, Windows-target-filtered release builder for a per-user/no-admin ZIP package with an explicit ten-file archive allowlist.
- Remapped the local repository prefix in the final Rust crate and added a package regression check so the release EXE cannot embed the build workspace path.
- Added a SHA-256 manifest and installer-side exact manifest verification; a modified package file is rejected before any install mutation.
- Generated complete third-party notices from each locked dependency's locally checked-out license files and checked the maintained dependency inventory against Cargo metadata.
- Added current-user install, normal-shutdown upgrade, optional HKCU start-with-Windows, Start Menu shortcut, default user-data removal, and explicit `-KeepUserData` uninstall behavior.
- Constrained install/uninstall paths and registry keys, rejected reparse-point directories, and used finite retry/replace behavior for files that may still be closing.
- Added a fully isolated package smoke under `target`: it uses temporary LocalAppData/AppData and a one-time HKCU test key, verifies tamper rejection, installs, upgrades, verifies hashes/startup/shortcut, uninstalls, and cleans all test state.
- Made the tray derive its start-with-Windows checkmark from an exact quoted-current-EXE match in the real HKCU Run value, preventing installer/settings display drift and stale-path false positives.
- Added an MIT project license, privacy notice, exact locked dependency inventory, and release guide with the unsigned developer-build boundary and Authenticode/Trusted Signing strategy.
- Added actionable UI diagnostics for missing App Server, bounded retry, schema mismatch, and unreported version without exposing raw user-agent/account data or fabricating quota values.
- Made the persisted `refresh_on_network_restore` setting effective both at callback registration and pure event mapping.
- Enforced the PRD card-open freshness rule: missing or at least 60-second-old data can refresh, while 0–59-second fresh data does not schedule another read.
- Preserved confirmed snapshot-level `rateLimitReachedType` as an aggregate reached signal. It drives exhausted UI/notification semantics without guessing whether `primary` or `secondary` caused it.
- Eliminated unconditional 5-second projection/repaint work: a 30-second timer now invalidates only when the normalized projection changes, while event delivery stays immediate and minute countdown updates remain intact.
- Increased the supervisor's idle process check from 50 ms to 250 ms after measurement showed needless wakeups; stdio arrival still wakes immediately and request deadlines remain 15 seconds. Dispatcher experiments at 50/250 ms were rejected because integration tests exposed write starvation at the existing shared transport lock boundary, so its reliable 25 ms value is unchanged.
- Contained each Windows App Server launch in a kill-on-close Job Object. This fixes an observed shutdown hang where `codex.cmd` exited but Node/Codex descendants retained redirected pipes; an offline process-tree regression now proves bounded cleanup.
- Changed `--shutdown-existing` to wait up to 10 seconds for the target process and return a nonzero status on incomplete cleanup; install/uninstall scripts now reject that failure instead of racing a locked executable.
- Verified the official Usage destination in the locally installed official Codex binary as `https://chatgpt.com/codex/settings/usage`; no page, cookie, or browser data was read.

### Dependency decision

- No production dependency was added. Packaging uses PowerShell/Windows facilities already present on supported systems, and dependency/license enumeration uses Cargo metadata.
- The ZIP approach avoids MSI/WiX/runtime bootstrapper overhead while still providing deterministic current-user install, upgrade, uninstall, integrity checks, and startup integration.
- Public signing is intentionally not automated because no repository-controlled signing identity exists; signing credentials must remain in an organization-controlled service or HSM.

### Files

- `LICENSE`
- `packaging/install.ps1`, `packaging/uninstall.ps1`
- `scripts/package.ps1`, `scripts/test-package.ps1`
- `docs/PRIVACY.md`, `docs/DEPENDENCIES.md`, `docs/RELEASE.md`
- `Cargo.toml`
- `src/alerts.rs`, `src/host_events.rs`, `src/main.rs`, `src/persistence.rs`, `src/quota.rs`, `src/ui_model.rs`, `src/windows_tray.rs`
- `tests/alerts.rs`, `tests/host_events.rs`, `tests/quota_parser.rs`, `tests/runtime_integration.rs`, `tests/ui_model.rs`
- `tests/fixtures/rate_limits_reached.json`
- `README.md`, `docs/TECH_DESIGN.md`, `docs/ROADMAP.md`, `docs/EXECUTION_LOG.md`

### Verification

- PowerShell parser check: zero syntax errors for all four packaging scripts.
- Isolated package smoke: passed repeatedly, including tamper rejection, install, upgrade, start-with-Windows, shortcut, default data removal, `-KeepUserData`, uninstall, and cleanup.
- Package privacy check confirms the final EXE contains neither the local repoRoot nor its slash-normalized form; a filename-only scan found no email-shaped literal in the package.
- Final package smoke artifact after process-tree and idle-timer hardening: 431,565-byte ZIP and 941,056-byte native EXE, both below the 20 MB PRD target.
- ZIP SHA-256: `DFEF4F7C9EDAF7F2E9153CE213C151DE97CE6D02D09634A1AB55032CF624B4B6`; the EXE Authenticode status remains explicitly `NotSigned`, matching the developer-build policy.
- Locked dependency inventory and generated notices covered every dependency returned by target-filtered Cargo metadata.
- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets --all-features -- -D warnings`: passed.
- `cargo test --all-targets`: passed, 85 tests after diagnostic, host-setting, card-age, reached-signal, startup-command, stable-projection, and descendant-process cleanup regressions were added.
- Windows 11 package operations were isolated and left no test registry key, install directory, user-data directory, shortcut, or package-host process.
- Final 30-minute native-host sample (three 10-minute fallback intervals): working set 10,272,768 → 10,444,800 bytes (+168 KiB), 141 handles unchanged, 6 threads at start/end with brief refresh peaks at 8, and CPU delta 2.125 seconds over 1,801 seconds. That is 0.118% of one core and 0.0054% using Windows' whole-system normalization on 22 logical processors, below the PRD 0.1% idle CPU target.
- The final real-process `--shutdown-existing` returned exit code 0 in 10 ms; after one second the tray PID and direct App Server child set were both absent, while the independent P1 soak PID remained alive.
- Repeated runtime integration stress runs exposed and removed assumptions about transient intermediate states and parallel fake-process scheduling. The final test asserts the observable active+one-pending boundary, and the complete serialized fake-process suite passes without changing production behavior.

### Remaining risks

- Windows 10 is not available in the current environment; its install/upgrade/uninstall/tray smoke remains mandatory before public release.
- The artifact is unsigned and must be labeled a developer build; official signing and provenance require an external organization-controlled identity.
- P1's 24-hour soak is still in progress. The PRD seven-day run remains a separate public-release quality objective.
- DPI/multi-monitor and per-row UI Automation validation still require a broader manual matrix.

### Next task

Run the final Rust and package gates, record the 30-minute native-host sample, create the P4 milestone commit, then allow the finite P1 soak to finish and perform the final orphan/privacy audit.

## 2026-07-16 — Final MVP validation and extended real-runtime soak

### Completed

- Preserved the independent release soak for 76,251 seconds (21 h 11 min). The user then explicitly ended the remaining monitoring period, so this result is not represented as a completed 24-hour run.
- The last sampled state at 74,723 seconds was generation 0, process Ready, data Fresh, one normalized window, exact schema match 0.137.0, a recorded successful refresh, and zero warnings.
- The extended run covered repeated 10-minute fallback intervals and a system sleep gap. A stale-preserving refresh state at 63,923 seconds returned to Fresh at 67,523 seconds without process restart or data loss.
- Final external resource observation before termination: about 2.55 MiB working set, 50.656 cumulative CPU seconds, 92 handles, 6 threads, and zero stderr bytes.
- After the explicit stop, the verified six-process soak tree (`runtime_soak`, command shim, Node, App Server, and console hosts) had zero remaining processes. No unrelated Codex process was terminated.
- P4 remains committed as `1c65c88`; no artifact was published and no remote branch was pushed.

### Final MVP boundary

- The user-approved 21-hour run satisfies the MVP requirement for one extended live run, but does not satisfy or replace the originally planned exact 24-hour gate or the PRD seven-day release-quality target.
- Windows 11 build/install/upgrade/uninstall/tray behavior, package privacy/integrity, 30-minute host resources, offline failure recovery, and read-only protocol behavior are verified.
- Windows 10, signed release provenance, broader DPI/multi-monitor coverage, and seven-day stability remain public-release gates, not claims of this local developer build.

### Verification

- Final Rust gates before the documentation closeout: `cargo fmt --all -- --check`, Clippy with `-D warnings`, all 85 tests, and `git diff --check` passed.
- Package smoke passed tamper rejection, install/upgrade, startup registration, shortcut, default removal, `-KeepUserData`, uninstall, cleanup, and build-path remapping.
- The soak log contains only normalized state and aggregate booleans/counters; stderr stayed empty and no raw response, token, email, or account identifier was persisted.

### Next task

Do not resume the stopped soak automatically. Before a public release, execute the Windows 10/DPI matrix, organization-controlled signing and provenance flow, and a separately scheduled seven-day soak.
## 2026-07-17 — 0.1.4 tray icon and visibility root-cause repair

### Completed

- Removed the initial stock-question tray icon path. Release resources are loaded synchronously from the current HINSTANCE before class registration; the window class, `WM_SETICON`, and the first `NIM_ADD` all receive owned product HICON handles. `NIM_SETVERSION` is applied immediately, and all owned handles are released only during shutdown.
- Added `scripts/verify-pe-icon.ps1`, which reads `RT_GROUP_ICON #101` and its `RT_ICON` children directly from the built PE. The verified release binary contains nine 32-bit frames: 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels.
- Moved shell callbacks to a dedicated `HWND_MESSAGE`. With notification icon version 4, only the low-word `WM_LBUTTONUP` event posts `WM_APP_TOGGLE_WINDOW`; double-click, button-down and `NIN_SELECT` events are ignored for toggling. Explorer restart is the only path that re-adds the icon.
- Replaced visibility reads, focus-loss timers and 200 ms suppression with the UI-thread `desired_visible` state. Exactly one queued toggle flips it and calls the corresponding show/hide helper; activation, repaint, refresh and DPI messages cannot change visibility.
- Added debug sequence numbers and monotonic elapsed milliseconds for shell events, toggle posts/handling, visibility transitions, icon loading and Shell_NotifyIcon operations. Logs contain no account or protocol data.

### Root causes

- The startup `?` was the notification-area icon selected by the initial offline projection (`IDI_QUESTION`), not the taskbar/Alt+Tab/title icon. The previous runtime modification happened after `NIM_ADD`.
- The second-click flash was a race between the tray callback and the old `WM_ACTIVATE/WA_INACTIVE` 80 ms hide timer. The callback now targets an independent message-only window and has a single event entry point.

### Verification

- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets --all-features --locked --offline -- -D warnings`: passed.
- `cargo test --all-targets --locked --offline`: passed (all offline tests).
- Release PE verification: passed for `RT_GROUP_ICON #101` and nine child icon frames.

## 2026-07-18 — 0.2.0 compact UI, persistent alerts and refresh modes

- Split title/plan Badge/status tone/refresh button projection and compacted quota panels while keeping the existing Win32/GDI style and DPI-derived geometry.
- Added versioned `alert-state.json`, SHA-256 local pseudonymous window identifiers, strict 50/20/10 crossings, non-retroactive enablement, UTC cycle tolerance and save-before-notify at-most-once behavior.
- Added Auto, fixed 5/15/30 minute and ManualOnly modes through one single-flight coordinator, dynamic stale thresholds and bounded failure backoff.
- Migrated legacy settings, aligned ZIP/Inno default-delete and explicit-keep semantics, and documented that disabling quota cache does not clear alert de-duplication state.
# 2026-07-18 — Codex CLI 0.144.5 protocol capability update

- Generated the stable App Server schema with `npx --yes @openai/codex@0.144.5 app-server generate-json-schema --out schemas/codex-0.144.5`; the generator reported `codex-cli 0.144.5`.
- Updated the protocol baseline to 0.144.5 and modeled top-level `rateLimitResetCredits` without rejecting future unknown fields.
- `availableCount` is authoritative; optional detail rows are used only for local-time expiry summaries. Reset-credit IDs and raw responses are never logged, displayed, copied or persisted.
- Replaced exact CLI-version gating with actual initialize/read capability results. Sparse rate-limit notifications retain the most recent full-read reset-credit snapshot.
- Added a compact reset-card UI state line and a bounded, privacy-reviewed “复制诊断信息” tray command.
# 2026-07-19 — WinUI 0.3.0 正式交付候选

- 正式程序集、单实例键、托盘 GUID、启动项和数据目录与既有产品身份对齐；保留 `--isolated-preview-data` 供无污染 smoke。
- `--shutdown-existing` 同时支持无运行实例时快速退出，以及通过 AppLifecycle 激活已有实例并正常关闭。
- 修正 `.cmd` CLI 的 `cmd.exe /d /s /c` 参数构造，并优先发现 `%APPDATA%\npm\codex.cmd`；使用 Codex CLI 0.144.5 完成一次真实只读读取。
- 生成 518 文件、约 218.96 MiB 的 self-contained x64 发布目录；排除 PDB 后，Inno Setup 7 成功生成 63,819,710 字节的 `CodexQuotaTray-0.3.0-setup.exe`，SHA-256 为 `ECDA14711A1080BFBAD2578D7925FBB84463E5C6C1CB16AE3CDE964FA06E8AEA`。
- 免安装 ZIP 为 92,713,546 字节，SHA-256 为 `5031E2C74418BF68C1A1C58721837DA032801DEB3558E95B7C8E3E8472688C8C`；递归清单覆盖全部文件且包内不含 PDB。
- 自包含 EXE smoke：读取后产生兼容额度缓存，工作集约 179.18 MiB；`--shutdown-existing` 返回 0 且主进程退出。
# 2026-07-19 — WinUI 0.3.1 托盘与 Acrylic 修复

- 托盘回调迁移到 `HWND_MESSAGE`，新增隐藏广播观察窗口、四次有界注册、Explorer 恢复和失败可访问降级。
- 删除窗口数量高度公式，改用 XAML 实际测量；窗口仅在工作区不足时启用滚动。
- 根层改为透明，主题切换为蓝色半透明 Acrylic 卡片、矢量额度图标和紧凑底栏；未新增任务/token 数据源。
- Release 构建 0 警告、0 错误；C# 离线测试增至 70 通过、1 个显式 live smoke 跳过。

# 2026-07-28 — WinUI 0.3.2 托盘、圆角与毛玻璃修复

- 定位到 `Shell_NotifyIconGetRect` 在 UI 线程注册路径中造成 UI/Shell 相互等待，注册状态会永久停在 `RetryPending`。注册路径不再同步查询矩形，位置查询改为后台缓存。
- 托盘回调改由独立隐藏工具窗口承载，启动时清理旧 host 项并以稳定产品 GUID、固定 `uID`、`NIM_ADD`、`NIM_SETVERSION` 重新注册；Explorer 重建不改变面板显隐。
- 保留无标题栏的系统细边框，启用 DWM 圆角，并把 XAML 外框圆角从 24 DIP 收敛到 12 DIP，消除四角双重曲率。
- 使用官方 `DesktopAcrylicController` 设置低 TintOpacity 与适度 LuminosityOpacity，同时降低外层和卡片遮罩不透明度；实机截图可见桌面色块透过并被模糊。
- 本地 smoke 验证两个连续 `WM_LBUTTONUP` 回调得到 `visible → hidden → visible`；Release 构建 0 警告、0 错误，C# 测试 70 通过、1 个显式 live smoke 跳过。

# 2026-07-28 — WinUI 0.3.3 托盘导出名与单一圆角修复

- 实机确认 0.3.2 的 callback HWND 存在，但 Explorer 对产品 GUID 返回 `E_FAIL`；通知区设置已为显示，排除隐藏菜单和旧安装包。
- 根因是 C# P/Invoke 名称缺少 Shell API 的下划线，后台任务发生未观察的 `EntryPointNotFoundException`。现已显式绑定 `Shell_NotifyIconW` 与 `Shell_NotifyIconGetRect`。
- callback 改为 message-only HWND，广播由独立 tool window 接收；Explorer 返回非空矩形后才标记 Registered。真实 Shell smoke 返回有效矩形并通过 100 次显示/隐藏循环。
- XAML 根层移除独立外框圆角和描边，窗口只使用 DWM 小圆角，消除双重曲率。

# 2026-07-28 — WinUI 0.3.4 窗口交互与额度配色

- 主面板改为置顶窗口，定位与拖动均使用显示器工作区和真实窗口外框约束，避免进入任务栏区域。
- 标题区域支持自由拖动；托盘隐藏后再次打开保留本次运行的位置，重启后仍从托盘附近开始。
- 外轮廓改为 DWM 标准圆角，标题简化为“Codex”，并移除额度重置行下方的分隔线。
- 额度强调色按原始剩余比例划分为大于 50% 绿色、20%–50% 琥珀色、低于 20% 红色；过期或无效数据保持灰色。
- WinUI Release 构建 0 警告、0 错误；C# 测试 78 通过、1 个显式 live smoke 跳过；Rust 格式、严格 Clippy 和全部测试通过。

# 2026-07-28 — WinUI 0.3.5 自动刷新反馈与拖动修复

- `MainViewModel` 现在完整应用运行时的 `IsRefreshing`，自动、定时、窗口打开和系统恢复刷新都会同步切换右上角 `ProgressRing` 并禁用重复刷新。
- 删除拖动过程中的逐帧 `AppWindow.Move` 校正，消除窗口靠近任务栏时应用定位与系统拖动竞争造成的回弹和闪烁。
- 初次显示仍保留托盘边距；隐藏后重开或内容重排仅在位置超出工作区时以零边距校正，允许窗口与任务栏上沿贴齐。
