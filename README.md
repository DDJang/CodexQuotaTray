# CodexQuotaTray

CodexQuotaTray is a read-only Rust client for the Codex App Server. It includes the P0 command-line probe, the long-running P1 service core, and a native Win32 tray host. It never implements reset-credit consumption.

Version 0.1.3 fixes the executable manifest resource and verifies Per-Monitor V2 awareness at process startup, preventing Windows from bitmap-scaling the quota card. It also sizes the card for the cursor's target monitor and uses point-correct ClearType Natural fonts. The transparent rounded multi-resolution icon and opaque DWM dark card remain unchanged, with no WebView, Electron, Chromium, or Windows App SDK runtime.

## Build and test

```powershell
cargo fmt --all -- --check
cargo check --all-targets
cargo clippy --all-targets --all-features -- -D warnings
cargo test --all-targets
```

Parser tests use only anonymized files under `tests/fixtures`; they do not start Codex or contact a real account.

## Build the Windows package

P4 produces a per-user, no-admin ZIP package from the locked dependency graph:

```powershell
pwsh -NoProfile -File .\scripts\package.ps1 -Cargo C:\Users\<user>\.cargo\bin\cargo.exe
pwsh -NoProfile -File .\scripts\test-package.ps1 -Cargo C:\Users\<user>\.cargo\bin\cargo.exe
```

The artifact is written to `dist\CodexQuotaTray-<version>-win-x64.zip`. The smoke script verifies the package allowlist and dependency notices, rejects a tampered file, and exercises isolated install, in-place upgrade, start-with-Windows registration, default data removal, and `-KeepUserData` uninstall. Current local packages are unsigned developer builds; read [the release guide](docs/RELEASE.md), [privacy notice](docs/PRIVACY.md), and [dependency inventory](docs/DEPENDENCIES.md) before distribution.

## Run the Windows tray

Debug builds default to deterministic demo data, so UI work never needs a live account:

```powershell
cargo run --bin codex-quota-tray-gui -- --demo
```

A release build starts the real read-only runtime and discovers `codex.cmd`, `codex.exe`, or `codex`:

```powershell
cargo build --release --bin codex-quota-tray-gui
.\target\release\codex-quota-tray-gui.exe
```

Use `--codex-bin PATH` to override discovery. The tray menu provides refresh, the official Usage link, non-sensitive cache, quota reminders, start-with-Windows, cache clearing, and exit. An installer or script can request the existing instance's normal cleanup path with:

```powershell
.\target\release\codex-quota-tray-gui.exe --shutdown-existing
```

The control command waits up to 10 seconds for the existing process and its App Server tree to
finish normal cleanup, and returns a nonzero exit code instead of reporting success early.

Keyboard access while the card is focused: `Enter` or `R` refreshes, `U` opens Usage, `F10` opens the system menu, and `Esc` hides the card. The release executable contains no WebView, Electron, or Chromium runtime.

`Tab`, Left, or Right changes the focused footer action; `Space` activates it. Rebuild the checked-in rounded preview and multi-resolution icon from `assets/app-icon-source.png` with `powershell -File .\scripts\generate-icon.ps1`; the generator validates transparent corners, an opaque center, and all nine ICO frames.

Card-open for missing or at least 60-second-old data, Windows resume, and restored system connectivity are routed through the same coordinator as manual and App Server events. Event bursts remain subject to the 10-second minimum interval and single in-flight request, while a 10-minute fallback works even when no system or App Server notification arrives. Quota notifications ask Windows to respect quiet time and never play application sound.

## Run the spike

```powershell
cargo run -- --watch-seconds 10
```

If Codex cannot be discovered automatically:

```powershell
cargo run -- --codex-bin C:\path\to\codex.exe --watch-seconds 10
```

The spike starts `codex app-server --stdio`, completes the initialization handshake, reads account and rate-limit state, listens for sparse rate-limit updates, then closes stdin and waits for the child to exit. It never prints raw protocol messages or account email data.

## Run the long-lived runtime soak

The finite soak harness exercises the production supervisor/runtime path and prints only normalized state plus aggregate counters:

```powershell
cargo run --example runtime_soak -- --seconds 300 --sample-seconds 30
```

For the 24-hour P1 gate, use `--seconds 86400`. Let the finite run complete so the harness can close stdin, reap the child, and report forced terminations. The harness never prints percentages, raw responses, email, account identifiers, or tokens.

## Local settings and cache

The persistence adapters use `%LOCALAPPDATA%\CodexQuotaTray\settings.json` and `quota-cache.json`. The tray enables its non-sensitive cache by default for immediate card display; the menu can disable and delete it at any time. The runtime writes only when that host setting is enabled. The cache contains only used percentage, window duration, reset time, last-success time, and the parsed CLI version. It excludes account/authentication data, plan type, limit identifiers/names, and raw protocol data. A restored cache is always marked stale until a live read succeeds.

## Regenerate schemas

```powershell
codex --version
codex app-server generate-json-schema --out schemas
```

Update `schemas/CODEX_VERSION` at the same time. The checked-in schemas represent the stable API of `codex-cli 0.137.0`; the runtime compares this record with the version advertised by the initialized App Server and exposes match, mismatch, or unreported as normalized state.
