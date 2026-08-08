# CodexQuotaTray privacy notice

CodexQuotaTray has two local, read-only clients: the Windows + WinUI tray app and a
personal-use Android APK. Neither client has analytics, an account backend, a web scraper,
an embedded browser, or a reset-credit consumption path.

## Shared boundaries

- WinUI reads quota data from a locally started Codex App Server; Android's current product
  path reads the Direct HTTPS usage endpoint after local OAuth authentication.
- Raw App Server/HTTP JSON is parsed in memory and discarded.
- stdout/stderr is drained only to prevent process deadlock; its text is not persisted or
  shown in the product UI.
- Neither client reads browser cookies, browser DOM content, conversations, project files,
  or code.
- Neither client logs access tokens, refresh tokens, email addresses, full account
  identifiers, raw limit identifiers, device codes, or raw authentication responses. WinUI
  delegates token persistence to Codex CLI; Android persists the minimum OAuth credentials
  (including the routing account ID) encrypted with Android Keystore in its App-private
  store so the personal APK can reopen without logging in again.
- Quota reads remain read-only. The clients do not purchase, consume reset credits, or send
  other account write requests.

## Windows + WinUI data flow

- WinUI starts the locally installed `codex app-server --stdio` and communicates over UTF-8
  JSONL.
- Codex CLI remains responsible for authentication; CodexQuotaTray does not read Codex token
  files.
- Clicking “official Usage” asks Windows to open the official Usage page in the default
  browser.

Windows settings, optional normalized quota cache, and reminder de-duplication state are
stored under `%LOCALAPPDATA%\CodexQuotaTray`. Preview uses the separate
`%LOCALAPPDATA%\CodexQuotaTray-WinUI-Preview` directory.

When phone Token sync is enabled, WinUI streams only `token_count` and timestamp fields from
the current user's Codex `sessions` and `archived_sessions`. It sends only daily aggregate
numbers and an all-history summary over the selected private LAN IPv4. Prompts, responses,
tool contents, project paths, session JSON and account identity are neither returned nor
logged. The 256-bit pairing secret is stored in the same App-private identity directory and
is never included in diagnostics.

The cache contains only normalized display data. `alert-state.json` may contain cycle times,
handled thresholds, and a locally derived pseudonymous key; original opaque IDs are not
stored. The WinUI settings page can disable or clear the quota cache. The uninstaller removes
local data unless the user explicitly keeps it.

## Android data flow

- The APK performs OAuth device-code login through the system browser and does not embed a
  WebView or inspect browser data.
- OAuth access/refresh credentials are stored in an App-private SharedPreferences store.
  A legacy `<filesDir>/codex-home/.codex/auth.json` can be parsed once for migration; the
  legacy file is retained and its contents are never logged or shown.
- Daily quota reads use `GET https://chatgpt.com/backend-api/wham/usage` with the access
  token and, when available, the account ID. The APK does not start App Server, Termux, or
  a local HTTP service on this path.
- Optional Token Usage sync is a separate, user-paired HTTP client restricted to RFC1918
  IPv4. Its pairing secret is encrypted with a separate Android Keystore key. The cached
  `token-usage-cache.json` contains only the aggregate schema returned by Windows and remains
  visible when Windows is offline.
- The APK does not persist a full quota response or history. It persists only the minimum
  alert de-duplication state and refresh timestamp. WorkManager and notifications are part
  of the current implementation; their real-device behavior remains a separate smoke check.
- `android:allowBackup` is disabled. Uninstalling the APK removes its App-private data;
  force-stop or ordinary process exit does not remove authentication.

The Android route is for personal use and is not an application-store privacy or compliance
program. Its current product scope is defined in [Android Roadmap](ANDROID_ROADMAP.md).
