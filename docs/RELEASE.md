# Release and packaging guide

## Artifact

0.5.0 uses a per-user, no-admin Inno Setup installer containing the folder-based, self-contained x64 WinUI application. Its client height is derived from the actual bottom edge of the final visible action row instead of the ScrollViewer viewport, and its DWM/card/progress geometry uses a coordinated rounded hierarchy. The former Rust ZIP workflow is available only through the `archive/rust-win32-final` Git tag and is not part of the current branch.

生成 WinUI 便携 ZIP：

```powershell
pwsh -NoProfile -File .\scripts\package-winui.ps1
```

输出为 `dist\CodexQuotaTray-<version>-win-x64.zip`。该 ZIP 是免安装便携归档，包含完整 WinUI 自包含发布目录、项目说明、隐私说明、依赖清单和递归 SHA-256 manifest；不包含旧 Rust 版的 `install.ps1` 或 `uninstall.ps1`。

正式发行安装器需要本机安装 Inno Setup 7（`ISCC.exe`）。构建命令：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1 -DotNet C:\path\to\dotnet.exe
```

输出为 `dist-inno\CodexQuotaTray-<version>-setup.exe`。脚本先执行 `publish-winui.ps1`，再把完整自包含目录递归加入安装器。安装器使用 per-user 模式，不请求管理员权限；默认创建开始菜单快捷方式并勾选 HKCU 登录启动。升级和卸载前会调用 `--shutdown-existing`，不强杀托盘进程。卸载器默认删除程序文件、启动项以及 `%LOCALAPPDATA%\CodexQuotaTray` 下的设置、额度缓存和提醒防重复状态；交互卸载可选择保留，静默卸载使用 `/KEEPUSERDATA` 显式保留。

当前用户安装、升级和卸载由 Inno Setup 产物负责。安装位置为 `%LOCALAPPDATA%\Programs\CodexQuotaTray`；再次运行安装器会先请求现有实例正常退出并执行原位升级。交互卸载可选择保留用户数据，静默卸载使用 `/KEEPUSERDATA` 显式保留。

## Signing strategy

Current local artifacts are intentionally unsigned and must be labeled as developer builds. They may trigger Windows SmartScreen warnings.

The in-package SHA-256 manifest detects accidental corruption and a file changed without updating the manifest. Because the developer package is unsigned, an attacker who can replace both files and manifest can forge that check; it is not an authenticity boundary. Public release authenticity requires the signing and separately published checksum process below.

A public release requires an organization-controlled Authenticode certificate or Microsoft Trusted Signing identity. The release operator should build in a controlled CI environment, sign the EXE and PowerShell scripts, verify every signature, regenerate the SHA-256 manifest after signing, create the ZIP, publish the ZIP checksum, and retain a provenance record. Credentials must stay in the signing service/HSM and never enter this repository or package.

No signing or publishing step is automated in this repository because the required identity is not available and the user explicitly prohibited automatic publication.

## Validation matrix

- Automated: WinUI restore/build/test, self-contained publish, ZIP manifest generation, PE icon verification and Inno Setup compilation.
- Completed manually: Windows 11 10.0.26200 x64 tray/runtime smoke.
- Required before public release: Windows 10 x64 install/upgrade/uninstall/tray smoke; Windows 11 stable-channel smoke; 100/125/150/200% DPI and multi-monitor positioning; signed artifact verification; real official Usage navigation.
- WinUI 0.4.0 发布验证使用 .NET host 的 `RT_GROUP_ICON #32512`，确认 16/20/24/32/40/48/64/128/256 帧，并执行 `test-winui-tray.ps1` 验证 Explorer 图标矩形和 100 次显隐切换。
- Rust 版 21 小时 11 分稳定性记录仅属于归档基线，不能替代 WinUI 的长期稳定性验证。
