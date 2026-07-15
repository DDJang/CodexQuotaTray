# CodexQuotaTray

P0 is a read-only Rust command-line feasibility spike for the Codex App Server. It intentionally contains no Windows tray UI and no reset-credit consumption path.

## Build and test

```powershell
cargo fmt --all -- --check
cargo check --all-targets
cargo clippy --all-targets --all-features -- -D warnings
cargo test --all-targets
```

Parser tests use only anonymized files under `tests/fixtures`; they do not start Codex or contact a real account.

## Run the spike

```powershell
cargo run -- --watch-seconds 10
```

If Codex cannot be discovered automatically:

```powershell
cargo run -- --codex-bin C:\path\to\codex.exe --watch-seconds 10
```

The spike starts `codex app-server --stdio`, completes the initialization handshake, reads account and rate-limit state, listens for sparse rate-limit updates, then closes stdin and waits for the child to exit. It never prints raw protocol messages or account email data.

## Regenerate schemas

```powershell
codex --version
codex app-server generate-json-schema --out schemas
```

Update `schemas/CODEX_VERSION` at the same time. The checked-in schemas represent the stable API of `codex-cli 0.137.0`.
