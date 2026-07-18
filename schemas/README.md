# App Server schemas

The checked-in protocol baseline is Codex CLI 0.144.5. Its generated stable schema is stored in `codex-0.144.5/`:

```powershell
npx --yes @openai/codex@0.144.5 --version
# codex-cli 0.144.5
npx --yes @openai/codex@0.144.5 app-server generate-json-schema --out schemas/codex-0.144.5
```

The generator was run without `--experimental`. Do not edit generated JSON manually. Regenerate the directory and update `CODEX_VERSION` whenever the runtime Codex CLI changes.
