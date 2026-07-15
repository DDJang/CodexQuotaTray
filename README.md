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

## Run the long-lived runtime soak

The finite soak harness exercises the production supervisor/runtime path and prints only normalized state plus aggregate counters:

```powershell
cargo run --example runtime_soak -- --seconds 300 --sample-seconds 30
```

For the 24-hour P1 gate, use `--seconds 86400`. Let the finite run complete so the harness can close stdin, reap the child, and report forced terminations. The harness never prints percentages, raw responses, email, account identifiers, or tokens.

## Regenerate schemas

```powershell
codex --version
codex app-server generate-json-schema --out schemas
```

Update `schemas/CODEX_VERSION` at the same time. The checked-in schemas represent the stable API of `codex-cli 0.137.0`; the runtime compares this record with the version advertised by the initialized App Server and exposes match, mismatch, or unreported as normalized state.
