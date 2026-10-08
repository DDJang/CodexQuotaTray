# Windows lightweight mode

- Area: Windows
- Baseline: `987c34d` (`codex/windows-chatgpt-quota-research`)
- Working branch: `codex/windows-lightweight-mode`
- Status: Active; local implementation, awaiting an authorized implementation commit
- Last verified: 2026-10-08

## Current decision

The user selected two independent interfaces after finding interaction problems in the earlier unload/remount
experiment. Ordinary mode keeps the original `MainWindow` and `SettingsWindow` implementation. Lightweight
mode uses a separate small `LightweightPanelWindow`. Switching hides/shows these windows; it does not unload,
remount, repeatedly destroy windows, clear heatmap projections, or force garbage collection.

Enable only in Settings. Clicking the tray, choosing Open Panel, or activating an existing lightweight instance
shows the small panel. Its switch can disable the mode; settings are saved through the existing serialized
settings path before the app restores the full panel. Save failures keep the mode enabled and display an error.
The small window explicitly calls `AppWindow.Show()` before activation, including when reopening after hiding.
Settings remain reachable from the tray menu. A failed tray registration retains the existing full-window fallback.

An additional startup defect caused the small switch to read false while lightweight startup had read true:
the settings model was constructed before runtime settings initialization. `QuotaRuntimeService` now accepts
the already loaded startup settings, so every UI sees the same initial state before a network read. An offline
regression verifies that immediate disabling persists correctly without requesting quota data.

Lightweight startup creates no full window. The original full window is constructed on first demand and then
kept until app exit. This avoids repeated native allocations, but memory need not return to the cold lightweight
level after using the full panel. The shared settings model is created once, independent of either interface.

Quota/Token reads, refresh schedules, cache submissions, notifications, automatic update checks, and enabled
phone synchronization retain their original runtime paths. The title-bar overlay uses the effective setting
`TitleBarQuotaOverlayEnabled || LightweightModeEnabled`, preserving the ordinary-mode preference.

Current contracts: [PRD](../../PRD.md), [TECH_DESIGN](../../TECH_DESIGN.md), [API_CONTRACT](../../API_CONTRACT.md).

## Heatmap regression

The Dev cache contained 90 daily records, including 80 recent nonzero days. The view model generated 119 cells
with 80 positive buckets, but reflection bindings in the item template left rendered controls at Bucket 0.
With typed `x:Bind`, a model Bucket 1 became control Bucket 1 and resolved the Dark blue brush `#3DB4E6FF`.
The immutable record uses `Mode=OneTime`; repeater template rebinding assigns each recycled item's values.
Automation text uses the same typed binding. No cache was cleared or rewritten to repair the display.

The fix is in [TokenUsageView.xaml](../../../windows/src/CodexQuotaTray.App/Views/TokenUsageView.xaml), with a
regression assertion in the existing theme/presentation source contract test.

## Historical experiments, superseded

All values below describe earlier implementations, not a promise for the final two-interface design. Measurements
were numeric process counters, without auth data or memory dumps. Production was observed read-only. Its longer
uptime, Release build, settings and cache history differ from Dev, so cross-process differences are not causal proof.

| Earlier phase | Dev working set MiB | Dev private bytes MiB | Observation |
| --- | ---: | ---: | --- |
| Ordinary warm UI | 239.271 | 124.588 | Initial baseline |
| Cold lightweight | 174.616 | 82.001 | No full UI created |
| Destroy/recreate, first release | 236 | 120 | Native resources remained |
| Destroy/recreate, later cycle | 254 | 138 | GDI grew from 58 to 97 |
| Reused shell, cold lightweight | 177.852 | 84.650 | 15.43 s sample; CPU 0.01381% of 22 logical processors |
| Compact-panel experiment, cold | 171.316 | 81.148 | Single point, not an idle average |
| Compact-panel experiment, shown | 190.789 | 92.488 | Full UI still absent; switch probe failed |

During the reused-shell cold sample, Production used 256.718 MiB working set and 268.977 MiB private bytes,
with CPU 0.0092%. That sample does not establish a CPU reduction. The CLI process chain continued to run.
The destroy/recreate and reusable-UserControl approaches were removed after the user's latest decision.

## Verification and limits

Final Windows verification uses `windows/scripts/verify-winui.ps1 -Mode Full`, covering the repository SDK and
NuGet configuration, format, Debug/Dev build and complete offline tests. No Release packaging or installation
is part of this change. Temporary diagnostic/probe code is removed before final delivery.

The two-interface implementation Full passed: 587 offline tests, zero build warnings/errors. All 161 local Markdown links across 76 files
and `git diff --check` passed. The original full main/settings view implementations are unchanged; only the
heatmap item-template binding and the new settings option change their XAML.

The Windows Computer Use helper failed with a sandbox/kernel error. Programmatic Dev checks can exercise the
actual switch event, persistence, window visibility and stable handles, but do not replace a physical tray-click
accessibility/screenshot check. Final two-interface measurements and probe results are recorded below when available.
Long-term native-resource behavior and CPU reduction remain unproven; cold startup is the primary expected saving.

### Two-interface Dev probe

Three rounds exercised the actual `ToggleSwitch.Toggled` handler, settings persistence, native window visibility,
and restoring the original full statistics panel. All rounds passed. The same full HWND (`8724422`) and compact
HWND (`1647454`) were reused across all rounds. These are ephemeral verification identifiers, not app identities.

| Phase | Working set MiB | Private bytes MiB |
| --- | ---: | ---: |
| Cold lightweight | 174.207 | 83.785 |
| First compact panel; full absent | 192.828 | 95.211 |
| First full panel, before statistics construction | 237.527 | 126.480 |
| Second compact panel; full retained | 247.160 | 136.164 |
| Second full statistics panel | 251.230 | 138.414 |
| Third compact panel; full retained | 248.980 | 136.113 |
| Third full statistics panel | 251.109 | 138.613 |

These are single-point process counters. The second/third rounds support stable window reuse in this short
test, not a long-run leak claim. Warm lightweight mode retains the full UI cost, as chosen for stability.

### Final normal Dev startup sample

After removing the probes and passing final Full, the normal Dev build was relaunched with lightweight enabled.
Native window enumeration showed the lifetime window and title-bar overlay, with no full or compact panel yet.
The 15.4 s process-counter average was 173.186 MiB working set / 83.265 MiB private bytes, GDI 12 / USER 40.
The concurrently running Production instance averaged 247.151 / 141.517 MiB, GDI 72 / USER 59; it was not stopped
or modified. CPU was 0.1476% Dev / 0.08761% Production normalized across 22 logical processors. This includes
Dev startup work and establishes no idle CPU benefit. The retained Dev CLI chain used approximately another
138.6 MiB working set / 84.8 MiB private bytes; main-process figures are not the whole app-server footprint.

The comparison uses different builds/settings/cache histories. It is an observed baseline, not proof that
the final feature alone produces the entire difference. The final working tree remains uncommitted on
`codex/windows-lightweight-mode`, based on `987c34d`.

### Requested lightweight / ordinary / running Production comparison

The user requested a fresh comparison on 2026-10-08. All main-process figures below are 20-second averages.
The current lightweight Dev instance was measured before constructing full UI, then with its small panel open.
The Computer Use helper remained unavailable (`windows sandbox failed: helper_unknown_error: setup refresh had errors`),
so only the Dev lightweight boolean was changed while Dev was stopped; the same final Debug executable was
restarted in ordinary mode, allowed 30 seconds to warm, then sampled. This is a restart comparison, not a hot-switch
memory-release test. Afterwards Dev's original lightweight boolean was restored and the normal Dev build relaunched.
Production PID 20296 was read-only throughout and kept running. No product code or build artifacts were changed.

| State | Main working set MiB | Main private bytes MiB | CLI chain working set MiB at sample end | Approximate combined working set MiB |
| --- | ---: | ---: | ---: | ---: |
| Dev lightweight background | 171.470 | 80.087 | 111.875 | 283.345 |
| Dev lightweight small panel | 197.227 | 98.492 | 111.875 | 309.102 |
| Dev ordinary full quota panel | 231.484 | 121.761 | 112.516 | 344.000 |
| Running Production, concurrent ordinary sample | 247.025 | 140.393 | 0 observed | 247.025 |

Production main working set stayed 247.0–248.0 MiB in the three samples. Dev quota source is `codexCli`,
Production quota source is `oAuth`; both Token sources are Local. The CLI chain includes cmd/conhost/node/codex.
This difference explains why the lightweight Dev process alone is smaller than Production, while its combined
footprint is larger. Combined values add main averages to end-of-sample child counters and are approximate;
summing working sets can also count shared pages more than once. They are not unique physical RAM measurements.

For the same Dev build and settings except lightweight mode, the unopened lightweight background main process
used 60.014 MiB less working set (25.93%) and 41.674 MiB less private bytes (34.23%) than ordinary startup.
With the small panel open, the main working-set saving was 34.257 MiB (14.80%). These figures do not imply that
enabling lightweight after constructing full UI reclaims those resources; the current design deliberately retains it.

### Debugging convenience follow-up

At the user's request, Settings → Advanced now has a default-enabled Show Error Dialogs switch. It controls
the app-owned previous abnormal-exit notice when opening the full panel; crash logging and pending notice markers
remain intact when disabled. The value auto-saves in the same settings file and rolls back on save failure.
Current behavior and persistence are recorded in the PRD/API contract. Final Full after this addition passed
590 offline tests, with zero build warnings/errors; the 161 local Markdown links and diff whitespace checks passed.

### Dev tray disappearance after moving to a worktree

The user reported that Dev's tray icon disappeared in both modes. Read-only Explorer queries returned
`0x80004005` and an empty rectangle for the declared Dev GUID, while Production still returned a valid rectangle.
The Dev GUID's `HKCU/Control Panel/NotifyIconSettings` entry pointed to the original `D:` checkout executable;
the running Dev executable was in the `C:` Codex worktree. This is the unsigned GUID/path association restriction
documented in [NOTIFYICONDATA troubleshooting](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw#troubleshooting).

The fix only changes how Dev identifies itself to Shell: message-only HWND + 16-bit uID, with empty `guidItem`
and no `NIF_GUID`. Add/modify/delete/version and rectangle queries use the same identity. Declared GUID constants,
Production/Preview GUID registration, app/data/LAN/startup identities and Explorer registry entries are unchanged.
No Explorer restart, cache deletion or registry mutation was performed. The tradeoff is that Dev no longer relies
on persistent GUID-based icon preferences across executable relocations.

The redundant ToggleSwitch header was removed, leaving the top lightweight title and the accessible switch name.
Full passed 590 offline tests, zero build warnings/errors. Read-only Explorer verification in ordinary mode
returned `S_OK`, Dev rectangle `1524,1032,1556,1080`; Production remained registered at `1556,1032,1588,1080`.
Lightweight mode returned the same valid rectangles with its own new callback HWND. Both modes therefore
registered concurrently with the running Production instance. Dev's pre-test mode was restored afterwards.
