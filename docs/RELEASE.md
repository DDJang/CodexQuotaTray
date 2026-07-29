# Release and packaging guide

## Artifact

0.4.2 uses a per-user, no-admin Inno Setup installer containing the folder-based, self-contained x64 WinUI application. Its client height is derived from the actual bottom edge of the final visible action row instead of the ScrollViewer viewport, and its DWM/card/progress geometry uses a coordinated rounded hierarchy. The older Rust ZIP workflow remains only as a regression and rollback baseline.

Build it from a clean checkout with the locked dependency graph:

```powershell
pwsh -NoProfile -File .\scripts\package.ps1 -Cargo C:\Users\<user>\.cargo\bin\cargo.exe
```

The output is `dist\CodexQuotaTray-<version>-win-x64.zip`. The script runs locked release compilation through `cargo rustc`, remaps the local repository path in the final crate, uses the checked-in Cargo.lock, restricts metadata to `x86_64-pc-windows-msvc`, verifies that every dependency has license material, and writes `MANIFEST.sha256` before compression. This is a repeatable build procedure, not a claim of bit-for-bit reproducibility: ZIP metadata and the local Rust toolchain can change artifact bytes and therefore the archive hash.

正式发行安装器需要本机安装 Inno Setup 7（`ISCC.exe`）。构建命令：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1 -DotNet C:\path\to\dotnet.exe
```

输出为 `dist-inno\CodexQuotaTray-<version>-setup.exe`。脚本先执行 `publish-winui.ps1`，再把完整自包含目录递归加入安装器。安装器使用 per-user 模式，不请求管理员权限；默认创建开始菜单快捷方式并勾选 HKCU 登录启动。升级和卸载前会调用 `--shutdown-existing`，不强杀托盘进程。卸载器默认删除程序文件、启动项以及 `%LOCALAPPDATA%\CodexQuotaTray` 下的设置、额度缓存和提醒防重复状态；交互卸载可选择保留，静默卸载使用 `/KEEPUSERDATA` 显式保留。

Install for the current user:

```powershell
Expand-Archive .\CodexQuotaTray-0.2.0-win-x64.zip .\CodexQuotaTray
pwsh -NoProfile -ExecutionPolicy Bypass -File .\CodexQuotaTray\install.ps1 -StartWithWindows
```

The install location is `%LOCALAPPDATA%\Programs\CodexQuotaTray`. Running the installer again performs an in-place upgrade after requesting normal shutdown of the existing instance. Uninstall with the installed `uninstall.ps1`; user data is deleted unless `-KeepUserData` is supplied.

## Signing strategy

Current local artifacts are intentionally unsigned and must be labeled as developer builds. They may trigger Windows SmartScreen warnings.

The in-package SHA-256 manifest detects accidental corruption and a file changed without updating the manifest. Because the developer package is unsigned, an attacker who can replace both files and manifest can forge that check; it is not an authenticity boundary. Public release authenticity requires the signing and separately published checksum process below.

A public release requires an organization-controlled Authenticode certificate or Microsoft Trusted Signing identity. The release operator should build in a controlled CI environment, sign the EXE and PowerShell scripts, verify every signature, regenerate the SHA-256 manifest after signing, create the ZIP, publish the ZIP checksum, and retain a provenance record. Credentials must stay in the signing service/HSM and never enter this repository or package.

No signing or publishing step is automated in this repository because the required identity is not available and the user explicitly prohibited automatic publication.

## Validation matrix

- Automated: locked release build, repository-path redaction, manifest verification, install, same-version overwrite/upgrade, isolated start-with-Windows registry value, shortcut creation, uninstall, default user-data removal, `-KeepUserData` preservation, size, and package-content checks.
- Completed manually: Windows 11 10.0.26200 x64 tray/runtime smoke.
- Required before public release: Windows 10 x64 install/upgrade/uninstall/tray smoke; Windows 11 stable-channel smoke; 100/125/150/200% DPI and multi-monitor positioning; signed artifact verification; real official Usage navigation.
- UI 0.2.0 validation additionally checks `RT_MANIFEST #1`, `RT_GROUP_ICON #101` and its 16/20/24/32/48/256 frames, effective Per-Monitor V2 process awareness, target-monitor window DPI, compact status/badge layout, opaque ClearType Natural rendering, light theme contrast, independent message-window tray toggling, taskbar-safe lower-right positioning, Windows 11 DWM corners, hover/pressed/focus states, and dynamic zero-to-three-window layouts.
- WinUI 0.4.0 发布验证使用 .NET host 的 `RT_GROUP_ICON #32512`，确认 16/20/24/32/40/48/64/128/256 帧，并执行 `test-winui-tray.ps1` 验证 Explorer 图标矩形和 100 次显隐切换。
- Stability: the completed extended run lasted 21 hours 11 minutes and ended on explicit user instruction, with no restart, warning, or orphan process observed. It is local MVP evidence, not a completed 24-hour or seven-day release-quality run.
