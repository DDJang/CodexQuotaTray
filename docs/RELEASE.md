# 构建与发布（Windows / WinUI）

本文只描述 Windows + WinUI 的构建与发布。Android APK 构建见
[`android/README.md`](../android/README.md)。

产品版本写在 [App 项目文件](../winui/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj)，SDK 由仓库根目录 [`global.json`](../global.json) 选择。所有命令从仓库根目录执行。准备正式 tag 前，建议先确认工作区干净并检查目标提交；`target/`、`dist/`、`dist-inno/`、`bin/`、`obj/` 和 `TestResults/` 都是本地产物，不应提交。

## 日常开发与正式发布边界

日常本地开发只构建和运行 Debug 的 **CodexQuotaTray Dev**。Dev 与 Production 使用独立的
single-instance identity、托盘 GUID、LocalAppData、启动项和 LAN listener identity，因此
不得在日常开发中生成、安装或修改正式 Release，也不得影响已安装的 Production。

正式 Windows Release 只由 GitHub Actions 从 `main` 提交上的 `windows-v*` tag 构建和发布。
本地 `publish`、ZIP 或 Inno 脚本只能用于验证发布目录或安装器输入，不能作为正式发布，也不应
作为日常开发步骤。

## 默认验证

日常 Dev 开发验证使用：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Quick
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Full
```

GitHub Actions 正式发布验证使用：

```powershell
pwsh -NoProfile -File .\scripts\verify-winui.ps1 -Mode Release
```

Quick/Full 都只构建 Debug/Dev；Release 模式才构建 Production Release，并在 Full 基础上生成
和检查 self-contained publish 目录。它不会生成 `dist/` ZIP、编译安装器、安装应用、签名或运行
交互式 smoke。

真实账户测试保持显式 opt-in，普通离线测试不需要真实 Codex 账户。

## Publish

需要单独生成 folder-based、self-contained x64 发布目录时执行：

```powershell
pwsh -NoProfile -File .\scripts\publish-winui.ps1
```

默认输出目录为 `target/winui-publish/`。脚本使用仓库 SDK 和 NuGet 配置，并检查应用 EXE、图标和运行时文件。

发布目录至少应包含：

- `codex-quota-tray-gui.exe`；
- `Assets/AppIcon.ico`；
- 应用和 Windows App SDK 所需运行时文件。

Publish 只是可运行目录，不等同于安装器或签名产物。

## 可选便携归档

项目不要求每次发布都生成 `dist/` ZIP。需要便携版时可以运行：

```powershell
pwsh -NoProfile -File .\scripts\package-winui.ps1
```

脚本默认先 publish，再打包运行文件、README、隐私和依赖说明，并生成 `VERSION` 与递归 `MANIFEST.sha256`。

ZIP 是额外下载形式，不是安装器的中间输入。当前 GitHub tag workflow 仍会附带该 ZIP，但它不是手工发布前置条件。

## Inno 安装器

Inno 安装器由现有脚本显式生成：

```powershell
pwsh -NoProfile -File .\scripts\package-inno.ps1
```

[`package-inno.ps1`](../scripts/package-inno.ps1) 从 App 项目文件读取版本并传给 [`CodexQuotaTray.iss`](../installer/CodexQuotaTray.iss)。缺少版本时安装器会停止编译，避免生成版本错误的安装包。

脚本默认先 publish，验证应用 EXE 和 PE 图标，再把 publish 目录交给 Inno。需要本机已安装 Inno Setup，并能找到 `ISCC.exe`。

安装器当前定义为：

- per-user、无需管理员权限；
- 安装到当前用户程序目录；
- 使用固定 AppId 进行原位升级；
- 安装和卸载前通过 Production `--shutdown-existing` 请求已有实例正常退出；
- 可选当前用户开机启动；
- 默认卸载用户数据，交互模式或 `/KEEPUSERDATA` 可保留。

## 0.x Preview/Beta 发布检查

个人维护的 0.x 版本按常用场景检查即可：

1. 运行 `verify-winui.ps1 -Mode Release`。
2. 生成 Inno 安装器。
3. 在常用 Windows 11 环境检查安装、启动、托盘菜单、主面板、设置、升级和卸载。
4. 为发布文件生成并公布 SHA-256。
5. 在 Release Notes 中说明当前是否签名，并列出已知限制。

Explorer 托盘 smoke 需要交互式桌面并关闭已有实例，因此只在确实需要时运行。真实账户 smoke 同样是可选检查，运行时只验证只读路径，不保存原始账户或额度响应。

当前产物通常未签名，Windows 可能显示 SmartScreen 提示；这不阻止 Preview/Beta 发布。Windows 10、更完整的 DPI/多显示器/高对比度组合、代码签名和长期稳定性验证，作为稳定版前逐步完成的项目记录在 [Roadmap](ROADMAP.md) 中。
