# App Server schemas

These files were generated from the locally installed stable App Server surface:

```powershell
codex --version
# codex-cli 0.137.0
codex app-server generate-json-schema --out schemas
```

The generator was run without `--experimental`. Do not edit generated JSON manually. Regenerate the directory and update `CODEX_VERSION` whenever the runtime Codex CLI changes.
