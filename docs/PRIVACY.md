# CodexQuotaTray privacy notice

CodexQuotaTray is a local, read-only companion for the installed Codex CLI. It has no analytics service, account backend, web scraper, embedded browser, or reset-credit consumption path.

## Data flow

- The app starts the locally installed `codex app-server` and uses its stdio protocol for `initialize`, `initialized`, and `account/rateLimits/read`.
- Codex CLI remains responsible for official authentication. CodexQuotaTray does not read browser cookies, Codex token files, project files, conversations, or browser DOM content.
- Raw App Server JSON is parsed in memory and discarded. stderr is drained only to prevent deadlock; its text is not persisted or shown.
- Clicking “official Usage” asks Windows to open `https://chatgpt.com/codex/settings/usage` in the user's default browser. No browser is opened otherwise.

## Local files

Settings, the optional quota cache, and the reminder de-duplication state are stored under `%LOCALAPPDATA%\CodexQuotaTray`.

The development-only `--isolated-preview-data` switch uses `%LOCALAPPDATA%\CodexQuotaTray-WinUI-Preview`.

The settings file can contain display preferences, refresh interval, reminder switches, the non-sensitive cache switch, and start-with-Windows preference. The non-sensitive cache is enabled by default for immediate card display and can be disabled from the Settings window, not the tray menu. The cache is a normalized projection containing `formatVersion`, `lastSuccessUtc`, optional `planType`, up to 32 windows with `sourceSlot`, `usedPercent`, `remainingPercent`, `percentageReliable`, optional `windowDurationMinutes` and `resetAtUtc`, plus optional `resetCreditAvailableCount` and `resetCreditEarliestExpiryUtc`.

`alert-state.json` has a schema version and stores UTC cycle times, the last reliable remaining percentage, handled thresholds, and a local pseudonymous identifier. When the service supplies a stable `limit_id`, the app stores its complete SHA-256 digest and never the original ID. This is a local pseudonymous identifier, not a claim of full anonymity. If no stable ID exists, a source-slot/duration/occurrence fallback is used.

The app does not persist email addresses, account identifiers, plan/auth state, raw limit identifiers or names, tokens, cookies, raw responses, reset-credit identifiers, conversations, or code.

## Control and deletion

- Disable “保存额度缓存” in the Settings window to delete only `quota-cache.json` and prevent further write-back. It intentionally does not delete `alert-state.json`, because doing so could cause duplicate threshold notifications.
- Use “清除额度缓存” in the Settings window to delete the quota cache immediately.
- The Inno uninstaller deletes settings, quota cache, and alert state by default. Interactive uninstall can choose to keep user data; silent uninstall keeps it only when `/KEEPUSERDATA` is explicitly supplied.
- The app produces no diagnostic log file by default.

When present, `rateLimitResetCredits.availableCount` is authoritative. Missing reset-credit data remains unavailable rather than being guessed as zero, and neither implementation contains a reset-credit consumption path.
