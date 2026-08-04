# App Server schemas

The checked-in protocol baseline is Codex CLI 0.144.5. Its generated stable schema is stored in `codex-0.144.5/`:

```powershell
npx --yes @openai/codex@0.144.5 --version
# codex-cli 0.144.5
npx --yes @openai/codex@0.144.5 app-server generate-json-schema --out schemas/codex-0.144.5
```

The generator was run without `--experimental`. Do not edit generated JSON manually. Update this directory and `CODEX_VERSION` only when a maintainer intentionally upgrades the checked-in protocol baseline to a new Codex CLI version.
