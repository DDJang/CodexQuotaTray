# CodexQuotaTray Development Instructions

## Project scope

CodexQuotaTray has two read-only, separately scoped clients for displaying
Codex quota windows and reset times.

- The current production entry point is C# with WinUI 3 under `winui/`.
- `android/` contains a personal-use standalone `arm64-v8a` APK. Its P0-P3
  baseline is complete; its only current roadmap goals are background refresh,
  notifications, a Widget, and boot startup.
- Android work must not modify `winui/`, and WinUI work must not change Android
  behavior, unless the owner explicitly requests cross-platform changes.
- The Rust/Win32 implementation is archived and does not participate in the
  current build, test, packaging, or release process.
- The WinUI client targets Windows 10 and Windows 11.
- Prefer low idle CPU usage and low memory usage.
- Do not use Electron, embed a browser runtime, or scrape web interfaces.
- Do not read browser cookies or store authentication tokens in logs or
  plaintext configuration.
- The MVP is always read-only. It must not consume reset credits or perform
  account writes.

## Documentation routing

Read only the material needed for the current task:

- Ordinary WinUI task: this file and the relevant XAML and code.
- Ordinary Android task: this file, `android/README.md`, and the relevant
  Android source and tests.
- WinUI product-semantics task: also read `docs/PRD.md`.
- Android product-semantics task: also read `docs/ANDROID_ROADMAP.md`.
- WinUI architecture or Core task: also read `docs/TECH_DESIGN.md`.
- Android runtime or architecture task: also read `android/README.md`.
- Protocol task: also read `docs/API_CONTRACT.md` and the relevant `schemas/`.
- WinUI release or packaging task: also read `docs/RELEASE.md`.
- Android release or packaging task: also read `android/README.md`.
- Windows roadmap or milestone task only: read `docs/ROADMAP.md`.
- Android roadmap or milestone task only: read `docs/ANDROID_ROADMAP.md`.

Do not require every task to load the complete documentation set.

## Task start

At the start of every task, report:

1. Repository root.
2. Current branch.
3. Current HEAD.
4. Working-tree status, including untracked files.
5. The validation level selected for the task.

Inspect existing implementation and tests before changing files. Make the
smallest coherent change that satisfies the requested scope.

## Module responsibilities

The following responsibilities describe the WinUI implementation:

- `Core/Protocol`: Codex App Server process and protocol transport.
- `Core/Runtime`: connection lifecycle, refresh coordination, and current state.
- `Core/Persistence`: settings, cache, and notification deduplication state.
- `Core/Presentation`: UI projection and ViewModels.
- `Core/Alerts`: notification reducer and threshold decisions.
- `App/Views`: WinUI windows and XAML.
- `App/Services`: tray integration, themes, and platform actions.
- `App/Interop`: Win32 interop boundaries.
- `App/Themes`: colors, styles, and visual resources.
- `Tests` and `FakeAppServer`: deterministic offline verification.

Keep these responsibilities separate. The UI must never parse raw JSON-RPC
responses directly.

For Android:

- `android/app/src/main/.../protocol`: App Server transport and typed quota input.
- `android/app/src/main/.../runtime`: embedded runtime and process lifecycle.
- `android/app/src/main/.../ui`: product projection; it must not parse raw RPC.
- `android/app/src/test`: deterministic offline Android regressions.
- `android/poc` and `android/bridge`: development diagnostics only, never APK
  runtime dependencies.

## Protocol and domain rules

- Treat App Server response fields as optional unless the generated schema
  marks them as required.
- Do not assume `primary` always represents five hours.
- Do not assume `secondary` always represents seven days.
- Use `windowDurationMins`, `limitId`, and `limitName` to identify quota windows.
- Treat `rateLimitResetCredits.availableCount` as authoritative.
- Do not silently replace unknown or missing values with zero.
- Preserve the last valid quota state when refresh fails.
- Distinguish fresh, refreshing, stale, offline, unauthenticated, and
  unavailable states.
- Use bounded retries and backoff when restarting App Server.

## Runtime identity boundaries

Production and Preview identities are separate compatibility boundaries.

- Production keeps its existing instance key, tray GUID, data directory, and
  startup registration.
- Demo uses static data with Preview process and tray identity.
- Live Preview uses isolated data with Preview process and tray identity.
- Preview and Demo must not read, write, or overwrite the Production startup
  registration.
- Never let one identity delete or replace the other identity's tray icon.

Do not change these without explicit owner approval:

- Product version.
- Protocol baseline.
- Dependency versions.
- Target framework or test SDK.
- Production instance key.
- Production tray GUID.
- Installer AppId.

## Working-tree safety

- Preserve all existing user changes, including untracked files.
- Never use `git reset`, `git clean`, `git restore`, or `git stash` unless the
  owner explicitly requests that exact operation.
- Never overwrite unrelated work to make a check pass.
- Do not install an SDK or change the user's NuGet configuration.
- Do not create an alternative `global.json`.
- Do not downgrade the target framework, Windows App SDK, or MSTest SDK.
- Do not add a production dependency without explaining maintenance status,
  binary-size impact, runtime impact, and why existing dependencies are
  insufficient.

Without an explicit request, do not:

- Commit changes.
- Push a branch.
- Create a pull request.
- Publish binaries.
- Generate ZIP or installer artifacts.
- Install, upgrade, uninstall, or sign the application.
- Run real-account or interactive Explorer smoke tests.

## Validation levels

For WinUI, use `scripts/verify-winui.ps1` from the repository root.

- `Quick`: toolchain report, repository-configured restore, Release x64 build,
  and `git diff --check`.
- `Full`: repository-configured restore, format verification, Release x64
  build, complete offline tests, and `git diff --check`.
- `Release`: Full validation plus the existing publish script and publish-output
  checks. It does not package, install, sign, or run smoke tests by default.

Choose validation proportionate to the change:

- Documentation-only: focused checks and `git diff --check`; use Quick when
  scripts or commands changed.
- XAML, theme, or window layout: Full plus task-authorized visual validation.
- Core, protocol, refresh, alerts, or persistence: Full and relevant regression
  tests.
- Tray or native window interop: Full; interactive tray smoke is explicit
  opt-in only.
- Packaging or installer: Release first; packaging and installation remain
  separately authorized operations.

For Android, use the repository Gradle Wrapper with JDK 17 and Android SDK 35:

- Kotlin/UI/protocol changes: `:app:testDebugUnitTest` and `:app:assembleDebug`.
- Runtime packaging or release changes: also run `:app:assembleRelease` with the
  owner-provided `CODEX_ANDROID_RUNTIME` input.
- Documentation-only Android changes: focused checks and `git diff --check`.
- ADB installation, real-account login, signing, and real-device smoke remain
  explicit opt-in operations.

Unit tests must use anonymized fixtures and must not require a real Codex
account. Add regression tests for parser defects, state transitions, missing or
null fields, unknown quota windows, and malformed responses.

## Environment failures

On the first failure, distinguish code failure from environment failure.

Environment failures include unavailable SDKs, NuGet permission/configuration
errors, test-host failures, and lack of an interactive Explorer desktop.

For an environment failure:

1. Record the working directory, original command, selected SDK, repository
   configuration source, and original error.
2. Make at most one correction retry using configuration already present in the
   repository.
3. Do not install an SDK, edit user NuGet settings, add another `global.json`, or
   downgrade framework and test dependencies.
4. If the retry fails, stop and report both failures without trying another
   workaround.

Do not mark a task complete while required checks are failing.

### Owner workstation Android paths

On the owner's current Windows workstation, do not assume the Android SDK is
under `%LOCALAPPDATA%`. The verified local paths are:

- Android SDK / `ANDROID_HOME` / `ANDROID_SDK_ROOT`: `D:\Android\Sdk`
- ADB: `D:\Android\Sdk\platform-tools\adb.exe`
- Currently available JDK: `C:\Users\18456\.jdks\openjdk-23.0.1`

The repository's supported Android toolchain remains JDK 17. Until a local JDK
17 is provided, the JDK path above is the already verified fallback for this
workstation; do not install or silently substitute another JDK.

In a sandboxed Codex session, the Gradle wrapper/cache and ADB device access are
outside the writable workspace. Request the required escalation on the first
Gradle or ADB command instead of first attempting a sandboxed wrapper download.
Set both Android SDK environment variables explicitly before invoking Gradle:

```powershell
$env:JAVA_HOME = 'C:\Users\18456\.jdks\openjdk-23.0.1'
$env:ANDROID_HOME = 'D:\Android\Sdk'
$env:ANDROID_SDK_ROOT = 'D:\Android\Sdk'
```

## Privacy and diagnostics

Never log:

- Access or refresh tokens.
- Browser cookies.
- User email addresses.
- Full account identifiers.
- Full reset-credit identifiers.
- Raw authentication responses.

Keep diagnostics concise and redact potentially identifying values.

## Task completion

Report only the current task:

- What changed.
- Files changed.
- Commands executed and their results.
- Skipped opt-in checks with complete names and reasons.
- Remaining limitations or unresolved risks.
- Final working-tree status.

Do not proactively implement or recommend a next task.
