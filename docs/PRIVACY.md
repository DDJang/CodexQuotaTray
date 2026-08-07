# CodexQuotaTray privacy notice

CodexQuotaTray has two local, read-only clients: the Windows + WinUI tray app and a
personal-use Android APK. Neither client has analytics, an account backend, a web scraper,
an embedded browser, or a reset-credit consumption path.

## Shared boundaries

- Both clients read quota data from a locally started Codex App Server.
- Raw App Server JSON is parsed in memory and discarded.
- stdout/stderr is drained only to prevent process deadlock; its text is not persisted or
  shown in the product UI.
- Neither client reads browser cookies, browser DOM content, conversations, project files,
  or code.
- Neither client logs or persists access tokens, refresh tokens, email addresses, full
  account identifiers, raw limit identifiers, device codes, or raw authentication responses.
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

The cache contains only normalized display data. `alert-state.json` may contain cycle times,
handled thresholds, and a locally derived pseudonymous key; original opaque IDs are not
stored. The WinUI settings page can disable or clear the quota cache. The uninstaller removes
local data unless the user explicitly keeps it.

## Android data flow

- The APK starts its embedded Android ARM64 Codex runtime from
  `applicationInfo.nativeLibraryDir` and connects to the App Server over loopback WebSocket.
- App login uses the App Server login flow and opens the system browser with an Android
  Intent. The APK does not embed a WebView or inspect browser data.
- Codex runtime stores authentication in the App-private
  `<filesDir>/codex-home/.codex` directory. CodexQuota does not copy, export, print, or expose
  that authentication data.
- The current Android baseline does not persist a quota cache and does not yet implement
  background refresh, notifications, Widget, or boot startup.
- `android:allowBackup` is disabled. Uninstalling the APK removes its App-private data;
  force-stop or ordinary process exit does not remove authentication.

The Android route is for personal use and is not an application-store privacy or compliance
program. Its current product scope is defined in [Android Roadmap](ANDROID_ROADMAP.md).
