# CodexQuotaTray privacy notice

CodexQuotaTray is a local, read-only companion for the installed Codex CLI. It has no analytics service, account backend, web scraper, embedded browser, or reset-credit consumption path.

## Data flow

- The app starts the locally installed `codex app-server` and uses its stdio protocol for `initialize`, `account/read`, and `account/rateLimits/read`.
- Codex CLI remains responsible for official authentication. CodexQuotaTray does not read browser cookies, Codex token files, project files, conversations, or browser DOM content.
- Raw App Server JSON is parsed in memory and discarded. stderr is drained only to prevent deadlock; its text is not persisted or shown.
- Clicking “official Usage” asks Windows to open `https://chatgpt.com/codex/settings/usage` in the user's default browser. No browser is opened otherwise.

## Local files

Settings and the optional quota cache are stored under `%LOCALAPPDATA%\CodexQuotaTray`.

The settings file can contain display preferences, refresh interval, reminder switches, the non-sensitive cache switch, and start-with-Windows preference. The non-sensitive cache is enabled by default for immediate card display and can be disabled from the tray menu. It is limited to percentages, window duration, reset time, last-success time, source slot, and the parsed Codex CLI version.

The app does not persist email addresses, account identifiers, plan/auth state, limit identifiers or names, tokens, cookies, raw responses, reset-credit identifiers, conversations, or code.

## Control and deletion

- Disable “保存非敏感额度缓存” to delete the quota cache and prevent further write-back.
- Use “清除本地额度缓存” to delete it immediately.
- The packaged uninstaller removes settings/cache by default. Pass `-KeepUserData` only if they should remain for a later reinstall.
- The app produces no diagnostic log file by default.

The version-matched App Server schema does not expose an authoritative reset-credit count. The UI therefore reports the count as unavailable and never guesses zero or consumes a credit.
